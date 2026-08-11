using Microsoft.Extensions.DependencyInjection;
using PrawoRAG.Domain.Llm;
using PrawoRAG.Domain.Retrieval;

namespace PrawoRAG.Eval;

public static class FollowUpProbe
{
    public static async Task RunAsync(IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var retriever = scope.ServiceProvider.GetRequiredService<IRetriever>();

        var tura1Q = "jak po 1 stycznia 2025 r. kwalifikować obiekty do podatku od nieruchomości — budowla czy budynek?";
        var tura1Answer = """
            Obiekt kwalifikuje się jako budowla, jeżeli jest obiektem budowlanym w rozumieniu przepisów prawa
            budowlanego, ale nie jest budynkiem ani obiektem małej architektury [7]. Kwalifikacja obiektu do
            podatku od nieruchomości odbywa się według następującego schematu: w pierwszej kolejności należy
            stwierdzić, czy dany obiekt jest obiektem budowlanym w rozumieniu prawa budowlanego [7]. Budowlą jest
            również urządzenie budowlane w rozumieniu przepisów prawa budowlanego, które jest związane z
            obiektem budowlanym [2, 3, 6, 7, 8]. Źródła nie zawierają informacji o zmianach w definicjach
            budynku lub budowli dla celów podatku od nieruchomości, które wchodziłyby w życie 1 stycznia 2025 r.
            """;
        var anchors = new List<string>
        {
            "[1] Ustawa z dnia [...] o zmianie ustawy o podatku dochodowym od osób fizycznych oraz ustawy o podatku dochodowym od osób prawnych",
            "[2] Wojewódzki Sąd Administracyjny w Poznaniu, I SA/Po 594/17",
            "[3] Naczelny Sąd Administracyjny, II FSK 1932/09",
            "[4] Wojewódzki Sąd Administracyjny w Poznaniu, I SA/Po 89/15",
        };
        var tura2Q = "a Art. 1a USTAWA O PODATKACH I OPŁATACH LOKALNYCH ?";
        var history = new List<ChatTurn> { new(tura1Q, tura1Answer, anchors) };

        RetrievalQuery Q(string text) => new() { Text = text, TopK = 8 };

        Console.WriteLine("=== PASS A: fresh question (no history) ===");
        var freshResult = await retriever.RetrieveAsync(Q(tura2Q), ct);
        Dump(freshResult);

        Console.WriteLine("\n=== PASS B: follow-up, replicating ChatService.AskAsync exactly ===");
        var rawQuery = Q(tura2Q);
        var rawResult = await retriever.RetrieveAsync(rawQuery, ct);
        Console.WriteLine($"-- raw (question alone) MaxSimilarity={rawResult.MaxSimilarity:F4}");
        Dump(rawResult);

        var ctxText = FollowUpQuery.Contextualize(history, tura2Q);
        var ctxQuery = Q(ctxText);
        var ctxResult = await retriever.RetrieveAsync(ctxQuery, ct);
        Console.WriteLine($"\n-- contextual MaxSimilarity={ctxResult.MaxSimilarity:F4}");
        Console.WriteLine($"-- ctxText: [{ctxText}]");
        Dump(ctxResult);

        var pickCtx = FollowUpQuery.PickContextual(rawResult.MaxSimilarity, ctxResult.MaxSimilarity);
        Console.WriteLine($"\n-- PickContextual -> {(pickCtx ? "CONTEXTUAL wins" : "RAW wins")}");
        var chosen = pickCtx ? ctxResult : rawResult;
        Console.WriteLine("-- CHOSEN result:");
        Dump(chosen);
    }

    private static void Dump(RetrievalResult r)
    {
        foreach (var c in r.Chunks.Take(8))
        {
            var title = c.Title.Length > 60 ? c.Title[..60] : c.Title;
            Console.WriteLine($"   score={c.Score:F4} sim={c.Similarity?.ToString("F4") ?? "null"} docType={c.DocType} eli={c.Locator?.EliId ?? "-"} art={c.Locator?.Article ?? "-"} title={title}");
        }
    }
}
