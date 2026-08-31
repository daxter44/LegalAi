using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Llm.Grounding;
using PrawoRAG.Storage;

namespace PrawoRAG.Eval;

/// <summary>
/// Raport z ŻYWEGO RUCHU (`--live-report`, Zadanie 16 planu ROU) — metryka nadrzędna (odsetek odmów)
/// i zachowanie bramek policzone na realnych odpowiedziach, także WSTECZ.
///
/// Dlaczego to jest tanie: <c>MessageEntity</c> od migracji <c>20260707123621</c> zapisuje dla każdej
/// odpowiedzi asystenta kontekst decyzji (<c>Abstained</c>, <c>CitationClean</c>,
/// <c>RetrievedSources</c>, <c>Model</c>) — komentarz encji mówi to wprost: „to materiał do golden
/// setu i kalibracji". Fazy 2/3 dołożyły <c>Route</c> i <c>Regenerated</c>. Nie trzeba niczego
/// instrumentować — trzeba to policzyć. Zero wywołań LLM, raport w całości deterministyczny.
///
/// KLUCZOWE rozróżnienie: odmowa PROGOWA (bramka abstynencji, kolumna <c>Abstained</c>) vs odmowa
/// TREŚCIOWA (model dostał źródła i sam orzekł, że nie odpowiadają — fraza w treści przy
/// <c>Abstained = false</c>). Ta druga jest u nas ważniejsza i bez rozdzielenia ich metryka
/// pokazywałaby połowę prawdy.
///
/// Zastrzeżenie co do danych: produkcji świadomie nie ma do czasu gotowości komercyjnej, więc dziś
/// „żywy ruch" to historia własnych testów plus to, co da zamknięta alfa. Ten raport nie wyczaruje
/// ruchu — robi z uruchomienia alfy przyrząd pomiarowy.
/// </summary>
public static class LiveReportRunner
{
    private sealed record Row(
        string Day, string? Model, int Answers,
        int ThresholdRefusals, int ContentRefusals, int DirtyCitations, int Regenerated,
        int RouteRetrieval, int RouteSmalltalk, int RouteUnknown,
        int? InputTokens, int? OutputTokens);

    public static async Task RunAsync(IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        await using var db = scope.ServiceProvider.GetRequiredService<PrawoRagDbContext>();

        // Tylko odpowiedzi asystenta — wiadomości użytkownika nie należą do mianownika żadnej z metryk.
        var messages = await db.Messages
            .Where(m => m.Role == "assistant")
            .Select(m => new
            {
                m.CreatedAt, m.Model, m.Abstained, m.CitationClean, m.Route, m.Regenerated, m.Content,
            })
            .ToListAsync(ct);

        if (messages.Count == 0)
        {
            Console.WriteLine("[live-report] brak odpowiedzi asystenta w bazie — nic do policzenia.");
            return;
        }

        var rows = messages
            .GroupBy(m => new { Day = m.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd"), m.Model })
            .OrderBy(g => g.Key.Day).ThenBy(g => g.Key.Model)
            .Select(g => new Row(
                g.Key.Day, g.Key.Model, g.Count(),
                ThresholdRefusals: g.Count(m => IsAnyRefusal(m.Abstained, m.Content) && !IsContentRefusal(m.Content)),
                ContentRefusals: g.Count(m => IsContentRefusal(m.Content)),
                DirtyCitations: g.Count(m => m.CitationClean == false),
                Regenerated: g.Count(m => m.Regenerated),
                RouteRetrieval: g.Count(m => m.Route == "retrieval"),
                RouteSmalltalk: g.Count(m => m.Route == "smalltalk"),
                RouteUnknown: g.Count(m => m.Route is null),
                InputTokens: null, OutputTokens: null))
            .ToList();

        var total = messages.Count;
        var contentRefusals = messages.Count(m => IsContentRefusal(m.Content));
        var thresholdRefusals = messages.Count(m => IsAnyRefusal(m.Abstained, m.Content)) - contentRefusals;
        var dirty = messages.Count(m => m.CitationClean == false);
        var regenerated = messages.Count(m => m.Regenerated);
        var smalltalk = messages.Count(m => m.Route == "smalltalk");

        Console.WriteLine();
        Console.WriteLine($"=== RAPORT Z ŻYWEGO RUCHU ({total} odpowiedzi asystenta) ===");
        Console.WriteLine();
        Console.WriteLine($"METRYKA NADRZĘDNA — odsetek odmów: {Pct(thresholdRefusals + contentRefusals, total)}");
        Console.WriteLine($"  odmowy progowe (bramka abstynencji): {thresholdRefusals} ({Pct(thresholdRefusals, total)})");
        Console.WriteLine($"  odmowy treściowe (model: źródła nie odpowiadają): {contentRefusals} ({Pct(contentRefusals, total)})");
        Console.WriteLine();
        Console.WriteLine($"BRAMKI:");
        Console.WriteLine($"  cytaty podejrzane (CitationClean=false): {dirty} ({Pct(dirty, total)})");
        Console.WriteLine($"  odpowiedzi regenerowane przez bramkę: {regenerated} ({Pct(regenerated, total)})");
        Console.WriteLine();
        Console.WriteLine($"ROUTER: bez bazy (small-talk) {smalltalk} ({Pct(smalltalk, total)}), " +
                          $"z bazą {messages.Count(m => m.Route == "retrieval")}, " +
                          $"bez oznaczenia (przed routerem) {messages.Count(m => m.Route is null)}");
        Console.WriteLine();

        Console.WriteLine("--- per dzień / model ---");
        Console.WriteLine("dzień      | model                | odp | odm.prog | odm.tresc | brudne | regen | smalltalk");
        foreach (var r in rows)
            Console.WriteLine(
                $"{r.Day} | {Trunc(r.Model ?? "?", 20),-20} | {r.Answers,3} | {r.ThresholdRefusals,8} | " +
                $"{r.ContentRefusals,9} | {r.DirtyCitations,6} | {r.Regenerated,5} | {r.RouteSmalltalk,9}");

        // Pytania z grup odmów — materiał do golden setu (dokładnie ten, o który prosi plan).
        var refusedQuestions = await RefusedQuestionsAsync(db, ct);
        if (refusedQuestions.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"--- pytania zakończone odmową ({refusedQuestions.Count}) — materiał do golden setu ---");
            foreach (var q in refusedQuestions.Take(30)) Console.WriteLine($"  • {Trunc(q, 140)}");
            if (refusedQuestions.Count > 30) Console.WriteLine($"  … i {refusedQuestions.Count - 30} więcej (pełna lista w JSONL)");
        }

        Directory.CreateDirectory("logs");
        var path = Path.Combine("logs", $"live-report-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");
        await using var writer = new StreamWriter(path);
        foreach (var r in rows)
            await writer.WriteLineAsync(JsonSerializer.Serialize(r));
        foreach (var q in refusedQuestions)
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { kind = "refused_question", question = q }));

        Console.WriteLine();
        Console.WriteLine($"[live-report] zapisano: {path}");
    }

    /// <summary>
    /// Pytania, po których odpowiedź była odmową — po parze (pytanie użytkownika → następna odpowiedź
    /// asystenta w tej rozmowie). Bez tego raport mówi ILE odmów, ale nie NA CO.
    /// </summary>
    private static async Task<List<string>> RefusedQuestionsAsync(PrawoRagDbContext db, CancellationToken ct)
    {
        var all = await db.Messages
            .OrderBy(m => m.ConversationId).ThenBy(m => m.CreatedAt)
            .Select(m => new { m.ConversationId, m.Role, m.Content, m.Abstained })
            .ToListAsync(ct);

        var refused = new List<string>();
        for (var i = 1; i < all.Count; i++)
        {
            var answer = all[i];
            var previous = all[i - 1];
            if (answer.Role != "assistant" || previous.Role != "user") continue;
            if (answer.ConversationId != previous.ConversationId) continue;

            if (answer.Abstained || answer.Content.Contains(AbstentionPolicy.Message))
                refused.Add(previous.Content);
        }
        return refused;
    }

    /// <summary>
    /// Odmowa TREŚCIOWA = model napisał frazę z reguły 3 („Nie mam wystarczających źródeł…" —
    /// <see cref="GroundedPrompt.RefusalMarker"/>), ale to NIE jest komunikat bramki
    /// (<see cref="AbstentionPolicy.Message"/> zaczyna się tym samym markerem, więc sam marker
    /// nie wystarcza do rozróżnienia). STAŁE, nie skopiowane napisy — inaczej zmiana komunikatu
    /// po cichu wyzerowałaby metrykę.
    /// </summary>
    private static bool IsContentRefusal(string content) =>
        content.Contains(GroundedPrompt.RefusalMarker, StringComparison.OrdinalIgnoreCase)
        && !content.Contains(AbstentionPolicy.Message, StringComparison.Ordinal);

    /// <summary>
    /// Dowolna odmowa — do metryki nadrzędnej. Kolumna <c>Abstained</c> OD wariantu A telemetrii
    /// (2026-08-31) obejmuje też odmowy treściowe, ale wiersze sprzed tej zmiany mają przy nich
    /// <c>false</c> — stąd dodatkowo dopasowanie treści, żeby raport liczył obie epoki danych tak samo.
    /// </summary>
    private static bool IsAnyRefusal(bool abstainedColumn, string content) =>
        abstainedColumn || content.Contains(GroundedPrompt.RefusalMarker, StringComparison.OrdinalIgnoreCase);

    private static string Pct(int part, int total) =>
        total == 0 ? "—" : $"{100.0 * part / total:0.0}%";

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
