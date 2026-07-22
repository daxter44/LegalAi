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
/// wrócić do wyniku po F5).
/// </summary>
public sealed class AnalysisRunner(
    IServiceScopeFactory scopes, IOptions<AnalysisOptions> options, CostGuard costGuard)
{
    public async Task RunAsync(AnalysisSession session, string userId, CancellationToken ct)
    {
        try
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
            catch { /* best-effort */ }

            session.SetStatus(AnalysisStatus.Analyzing);
            using var gate = new SemaphoreSlim(Math.Max(1, options.Value.MaxParallelism));
            await Task.WhenAll(session.Units.Select(async unit =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    session.SetUnitResult(await AnalyzeUnitAsync(session.Prompt, unit, userId, ct));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    session.SetUnitResult(new UnitAnalysis(
                        unit.Index, unit.Heading, UnitVerdict.Error, null, [], Error: ex.Message));
                }
                finally { gate.Release(); }
            }));

            session.SetStatus(AnalysisStatus.Summarizing);
            string? summary = null;
            try { summary = await SummarizeAsync(session, userId, ct); }
            catch { /* raport per-jednostka stoi bez streszczenia */ }
            session.Complete(summary);
        }
        catch (Exception ex)
        {
            session.Fail(ex.Message);
        }
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
}
