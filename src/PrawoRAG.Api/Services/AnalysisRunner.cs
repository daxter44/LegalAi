using PrawoRAG.Llm.Analysis;
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
    IServiceScopeFactory scopes, IOptions<AnalysisOptions> options, CostGuard costGuard, IAnalysisStore store,
    ILogger<AnalysisRunner>? logger = null)
{
    public async Task RunAsync(AnalysisSession session, string userId, CancellationToken ct)
    {
        // Rekord powstaje NA STARCIE (status Analyzing) — analiza w toku jest widoczna na liście po F5.
        await Persist($"create {session.Id}", () => store.CreateAsync(
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
            await Persist($"complete {session.Id}", () => store.CompleteAsync(session.Id, summary, CancellationToken.None));
        }
        catch (OperationCanceledException)
        {
            // Powód z sesji (anulowanie usera / sweep TTL); fallback UserCancelled — jedyna droga do
            // OCE bez Cancel() to token, którego nikt inny nie trzyma. Restart procesu tu nie dociera
            // (proces ginie) — tamten przypadek zamiata MarkAllInterruptedAsync na starcie.
            var reason = session.CancelReason ?? InterruptReason.UserCancelled;
            session.SetStatus(AnalysisStatus.Interrupted);
            await Persist($"interrupt {session.Id}",
                () => store.MarkInterruptedAsync(session.Id, reason, CancellationToken.None));
        }
        catch (Exception ex)
        {
            session.Fail(ex.Message);
            await Persist($"fail {session.Id}", () => store.FailAsync(session.Id, ex.Message, CancellationToken.None));
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
                result = await AnalyzeUnitAsync(session, unit, userId, ct);
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
                await Persist($"unit {session.Id}/{result.Index}", () => store.UpsertUnitAsync(session.Id, result, CancellationToken.None));
            }
        }));
    }

    /// <summary>Faza map jednej jednostki: capy pojemności (CostGuard z chargePlan:false — pula planu
    /// naliczona per DOKUMENT na starcie, nie per fragment), potem JEDNO ponowienie na klasę błędów, które retry faktycznie
    /// leczy (diagnoza 2026-09-02: 3 z 5 BŁĘDÓW sesji regulamin.pdf to transport — 500 z LLM, zerwane
    /// HTTP, transient DB; do tego werdykt „?" = pusta odpowiedź modelu). Limit planu NIE jest przy
    /// ponowieniu pobierany drugi raz — retry naprawia błąd systemu, nie jest nowym zapytaniem
    /// użytkownika. UI widzi stan przez <see cref="AnalysisSession.MarkUnitLive"/>.</summary>
    private async Task<UnitAnalysis> AnalyzeUnitAsync(AnalysisSession session, DocUnit unit, string userId, CancellationToken ct)
    {
        if (await costGuard.TryAcquireAsync(userId, ct, chargePlan: false) is { Allowed: false } limit)
            return new UnitAnalysis(unit.Index, unit.Heading, UnitVerdict.Error, null, [],
                Error: limit.Message);

        session.MarkUnitLive(unit.Index, UnitLiveState.Running);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var result = await AnalyzeUnitOnceAsync(session, unit, userId, ct);
                // Pusta/niesparsowalna odpowiedź modelu (badge „?") — jednorazowe ponowienie zanim
                // werdykt trwale wyląduje jako Unknown (rekomendacja P1.4 raportu niezawodności).
                if (attempt == 1 && result is { Verdict: UnitVerdict.Unknown } &&
                    string.IsNullOrWhiteSpace(result.Answer))
                {
                    await RetryPauseAsync(session, unit.Index, ct);
                    continue;
                }
                return result;
            }
            catch (Exception ex) when (attempt == 1 && IsTransient(ex))
            {
                await RetryPauseAsync(session, unit.Index, ct);
            }
        }
    }

    /// <summary>Krótka pauza między próbami (transient zwykle mija w sekundy) z widocznym dla UI
    /// stanem „ponawiam" — użytkownik widzi, że system reaguje, a nie że coś się zawiesiło.</summary>
    private static async Task RetryPauseAsync(AnalysisSession session, int index, CancellationToken ct)
    {
        session.MarkUnitLive(index, UnitLiveState.Retrying);
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        session.MarkUnitLive(index, UnitLiveState.Running);
    }

    /// <summary>Błąd przejściowy = transport (HTTP do LLM, timeout, socket) albo transient bazy
    /// (Npgsql). Sprawdzane po łańcuchu Inner/Aggregate, bo EF i HttpClient opakowują źródło.</summary>
    private static bool IsTransient(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is AggregateException agg) return agg.InnerExceptions.Any(IsTransient);
            if (e is HttpRequestException or TimeoutException or System.Net.Sockets.SocketException) return true;
            if (e is Npgsql.NpgsqlException { IsTransient: true }) return true;
        }
        return false;
    }

    /// <summary>Jedna próba analizy jednostki: świeży scope DI, drenaż strumienia zdarzeń czatu do wyniku.</summary>
    private async Task<UnitAnalysis> AnalyzeUnitOnceAsync(AnalysisSession session, DocUnit unit, string userId, CancellationToken ct)
    {
        var userPrompt = session.Prompt;
        using var scope = scopes.CreateScope();
        var chat = scope.ServiceProvider.GetRequiredService<IChatService>();

        var answer = new StringBuilder();
        IReadOnlyList<ChatSource> sources = [];
        PrawoRAG.Llm.Grounding.CitationCheck? check = null;
        string? abstainMessage = null;
        string? error = null;

        // forceRetrieval: jednostka analizowanego dokumentu NIGDY nie jest small-talkiem, a jej treść
        // (preambuła, komparycja, dane stron) często nie zawiera żadnego tokenu prawnego — czyli
        // bezpiecznik by nie zadziałał i decyzja spadłaby na router. Jego pomyłka dałaby WERDYKT
        // ANALIZY bez retrievalu, nieugruntowany, w dokumencie o charakterze audytowym. Pytanie
        // routera o to jest bezcelowe, a ryzykowne — więc go tu nie pytamy.
        await foreach (var e in chat.AskAsync(
            AnalysisPrompts.MapQuestion(userPrompt, unit), [], null, forceRetrieval: true, ct))
            switch (e)
            {
                case SourcesEvent s: sources = s.Sources; break;
                case TokenEvent t: answer.Append(t.Text); break;
                // Sygnał życia dla UI: „🧠 myśli… (N zn.)" przy jednostce w toku zamiast martwego
                // „…" przez cały czas rozumowania (naprawa 2026-09-02, lustro czatu).
                case ReasoningDeltaEvent rd: session.ReportUnitThinking(unit.Index, rd.Text.Length); break;
                case AbstainEvent a: abstainMessage = a.Message; break;
                case DoneEvent d: check = d.Check; break;
                case ChatErrorEvent err: error = err.Message; break;
            }

        await costGuard.RecordAsync(userId, answer.Length, ct);

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
        if (await costGuard.TryAcquireAsync(userId, ct, chargePlan: false) is { Allowed: false }) return null;

        using var scope = scopes.CreateScope();
        var llm = scope.ServiceProvider.GetRequiredService<ILlmProvider>();
        var request = new LlmRequest
        {
            Messages =
            [
                new(ChatRole.System, AnalysisPrompts.SummarySystemPrompt),
                new(ChatRole.User, AnalysisPrompts.SummaryInput(session.Prompt,
                    results.Select(r => new UnitDigest(r.Heading, r.Verdict, r.Answer ?? r.Error ?? "")))),
            ],
            Temperature = 0,
        };

        var sb = new StringBuilder();
        await foreach (var delta in llm.StreamCompletionAsync(request, ct))
            sb.Append(delta);
        await costGuard.RecordAsync(userId, sb.Length, ct);
        return sb.Length > 0 ? sb.ToString().Trim() : null;
    }

    /// <summary>Zapis best-effort: awaria bazy nie może zablokować ani zwalić analizy — ale NIE po
    /// cichu (naprawa 2026-09-02): pojedynczy transient DB potrafił bezpowrotnie skasować gotowy
    /// wynik jednostki (§ 12 sesji 01a05ec7) bez żadnego śladu. Przed poddaniem się JEDNO ponowienie
    /// samego zapisu (wynik już jest w pamięci — koszt zerowy vs utrata wywołania LLM), a każde
    /// połknięcie jest logowane z kontekstem.</summary>
    private async Task Persist(string context, Func<Task> op)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { await op(); return; }
            catch (Exception ex) when (attempt == 1 && IsTransient(ex))
            {
                logger?.LogWarning(ex, "Zapis analizy nieudany ({Context}) — ponawiam", context);
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Zapis analizy UTRACONY ({Context})", context);
                return;
            }
        }
    }
}
