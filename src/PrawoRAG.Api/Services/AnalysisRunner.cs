using System.Text;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain.Embeddings;
using PrawoRAG.Domain.Llm;

namespace PrawoRAG.Api.Services;

/// <summary>
/// Orkiestrator analizy dokumentu (SPK-3, map-reduce): per jednostka pełny ChatService (retrieval
/// korpusu + ugruntowanie + abstynencja + anty-fabrykacja za darmo), równoległość ograniczona
/// semaforem (lokalny LLM na jednej karcie i tak generuje sekwencyjnie — patrz
/// <see cref="AnalysisOptions.MaxParallelism"/>), potem JEDNO wywołanie LLM na streszczenie.
/// Awaria jednej jednostki nie wali sesji (werdykt BŁĄD); awaria streszczenia nie wali raportu
/// (raport per-jednostka jest składany mechanicznie w UI). Scope DI PER JEDNOSTKA — wspólny scoped
/// DbContext nie jest thread-safe. Singleton: działa w tle poza obwodem Blazora (id sesji pozwala
/// wrócić do wyniku po F5). Persystencja raportu (AN-3) w całości BEST-EFFORT — analiza dla
/// użytkownika ma priorytet nad zapisem.
/// </summary>
public sealed class AnalysisRunner(
    IServiceScopeFactory scopes, IOptions<AnalysisOptions> options, CostGuard costGuard, IAnalysisStore store)
{
    public async Task RunAsync(AnalysisSession session, string userId, CancellationToken ct)
    {
        // Rekord powstaje NA STARCIE (status Analyzing) — analiza w toku jest widoczna na liście po F5.
        await Persist(() => store.CreateAsync(
            session.Id, userId, session.FileName, session.PageCount, session.Prompt,
            session.Units.Count, session.UnitsTruncated, CancellationToken.None));

        await ExecuteAsync(session, userId, async () =>
        {
            // Przygotowanie: embeddingi jednostek (routing dopytań, SPK-6). Best-effort — bez nich
            // dopytania degradują się do trybu przekrojowego, analiza działa dalej.
            try
            {
                using var scope = scopes.CreateScope();
                var embedder = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
                session.SetUnitEmbeddings(await embedder.EmbedPassagesAsync(
                    session.Units.Select(u => u.Text).ToList(), ct));
            }
            catch (OperationCanceledException) { throw; }
            catch { /* best-effort */ }

            session.SetStatus(AnalysisStatus.Analyzing);
            await MapUnitsAsync(session, userId, session.Units, ct);
        }, ct);
    }

    /// <summary>Ponowienie wskazanych jednostek (AN-4; UI podaje te z werdyktem BŁĄD). Wymaga ŻYWEJ
    /// sesji — treść jednostek istnieje tylko w pamięci. Po ponowieniu streszczenie jest REGENEROWANE
    /// (stare mogło opisywać błędy, których już nie ma) i nadpisywane w rekordzie DB.</summary>
    public async Task RetryUnitsAsync(AnalysisSession session, string userId, IReadOnlyList<int> indexes, CancellationToken ct)
    {
        if (indexes.Count == 0) return;
        var byIndex = session.Units.ToDictionary(u => u.Index);
        var units = indexes.Where(byIndex.ContainsKey).Select(i => byIndex[i]).ToList();
        foreach (var u in units) session.MarkUnitPending(u.Index);

        await ExecuteAsync(session, userId, () => MapUnitsAsync(session, userId, units, ct), ct);
    }

    /// <summary>Wspólny szkielet przebiegu: ciało (map) → streszczenie → Complete; anulowanie →
    /// Interrupted (częściowy raport czytelny, NIE awaria); wyjątek → Failed. Stany lustrzane w DB.</summary>
    private async Task ExecuteAsync(AnalysisSession session, string userId, Func<Task> body, CancellationToken ct)
    {
        try
        {
            await body();

            session.SetStatus(AnalysisStatus.Summarizing);
            string? summary = null;
            try { summary = await SummarizeAsync(session, userId, ct); }
            catch (OperationCanceledException) { throw; }
            catch { /* raport per-jednostka stoi bez streszczenia */ }
            session.Complete(summary);
            await Persist(() => store.CompleteAsync(session.Id, summary, CancellationToken.None));
        }
        catch (OperationCanceledException)
        {
            session.SetStatus(AnalysisStatus.Interrupted);
            await Persist(() => store.MarkInterruptedAsync(session.Id, CancellationToken.None));
        }
        catch (Exception ex)
        {
            session.Fail(ex.Message);
            await Persist(() => store.FailAsync(session.Id, ex.Message, CancellationToken.None));
        }
    }

    /// <summary>Faza map dla wskazanych jednostek: semafor równoległości, wynik do sesji + upsert
    /// do DB W TRAKCIE (kill procesu w połowie = częściowy raport, nie nic).</summary>
    private async Task MapUnitsAsync(AnalysisSession session, string userId, IReadOnlyList<DocUnit> units, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, options.Value.MaxParallelism));
        await Task.WhenAll(units.Select(async unit =>
        {
            await gate.WaitAsync(ct);
            UnitAnalysis? result = null;
            try
            {
                result = await AnalyzeUnitAsync(session.Prompt, unit, userId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = new UnitAnalysis(
                    unit.Index, unit.Heading, UnitVerdict.Error, null, [], Error: ex.Message);
            }
            finally { gate.Release(); }
            if (result is not null)
            {
                session.SetUnitResult(result);
                await Persist(() => store.UpsertUnitAsync(session.Id, result, CancellationToken.None));
            }
        }));
    }

    /// <summary>Faza map jednej jednostki: dzienne limity kosztów (CostGuard — dokument to KILKANAŚCIE
    /// wywołań LLM, każde liczone), świeży scope DI, drenaż strumienia zdarzeń czatu do wyniku.</summary>
    private async Task<UnitAnalysis> AnalyzeUnitAsync(string userPrompt, DocUnit unit, string userId, CancellationToken ct)
    {
        if (!costGuard.TryAcquire(userId, out var reason))
            return new UnitAnalysis(unit.Index, unit.Heading, UnitVerdict.Error, null, [],
                Error: CostGuard.LimitMessage(reason));

        using var scope = scopes.CreateScope();
        var chat = scope.ServiceProvider.GetRequiredService<IChatService>();

        var answer = new StringBuilder();
        IReadOnlyList<ChatSource> sources = [];
        PrawoRAG.Llm.Grounding.CitationCheck? check = null;
        string? abstainMessage = null;
        string? error = null;

        await foreach (var e in chat.AskAsync(AnalysisPrompts.MapQuestion(userPrompt, unit), [], null, ct))
            switch (e)
            {
                case SourcesEvent s: sources = s.Sources; break;
                case TokenEvent t: answer.Append(t.Text); break;
                case AbstainEvent a: abstainMessage = a.Message; break;
                case DoneEvent d: check = d.Check; break;
                case ChatErrorEvent err: error = err.Message; break;
            }

        costGuard.Record(userId, answer.Length);

        if (error is not null)
            return new UnitAnalysis(unit.Index, unit.Heading, UnitVerdict.Error, null, sources, Error: error);
        if (abstainMessage is not null)
            return new UnitAnalysis(unit.Index, unit.Heading, UnitVerdict.NoSources, abstainMessage, []);

        var (verdict, text) = AnalysisPrompts.ParseVerdict(answer.ToString());
        return new UnitAnalysis(unit.Index, unit.Heading, verdict, text, sources, check);
    }

    /// <summary>Faza reduce: JEDNO wywołanie LLM (bez retrievalu — streszcza wyłącznie dostarczone
    /// wyniki) na kompaktowym digestcie werdyktów. Null = limit kosztów albo pusty raport.</summary>
    private async Task<string?> SummarizeAsync(AnalysisSession session, string userId, CancellationToken ct)
    {
        var results = session.Snapshot().Results.Where(r => r is not null).Cast<UnitAnalysis>().ToList();
        if (results.Count == 0) return null;
        if (!costGuard.TryAcquire(userId, out _)) return null;

        using var scope = scopes.CreateScope();
        var llm = scope.ServiceProvider.GetRequiredService<ILlmProvider>();
        var request = new LlmRequest
        {
            Messages =
            [
                new(ChatRole.System, AnalysisPrompts.SummarySystemPrompt),
                new(ChatRole.User, AnalysisPrompts.SummaryInput(session.Prompt, results)),
            ],
            Temperature = 0,
        };

        var sb = new StringBuilder();
        await foreach (var delta in llm.StreamCompletionAsync(request, ct))
            sb.Append(delta);
        costGuard.Record(userId, sb.Length);
        return sb.Length > 0 ? sb.ToString().Trim() : null;
    }

    /// <summary>Zapis best-effort: awaria bazy nie może zablokować ani zwalić analizy.</summary>
    private static async Task Persist(Func<Task> op)
    {
        try { await op(); } catch { /* best-effort */ }
    }
}
