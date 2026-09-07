namespace PrawoRAG.Ingestion.EurLex;

/// <summary>Relacje aktu ustalone w CELLAR-ze: co zmienia i co uchyla (CELEX-y celów).</summary>
public sealed record EuActRelations(IReadOnlyList<string> Amends, IReadOnlyList<string> Repeals)
{
    public static readonly EuActRelations None = new([], []);
}

/// <summary>
/// Klasa aktu UE — decyduje, czy akt trafia do WEKTORÓW, czy tylko do metadanych.
/// Powód (pomiar 2026-08-26 na całej populacji 7 756 obowiązujących rozporządzeń i dyrektyw):
/// 4 003 akty (52%) tylko zmieniają inne akty, a ich tekst operacyjny to instrukcja zmiany
/// („w załącznikach II, III i IV do rozporządzenia (WE) nr 396/2005 wprowadza się zmiany zgodnie
/// z załącznikiem"). 3 662 z nich są już WCHŁONIĘTE w teksty skonsolidowane, które ingestujemy —
/// więc ich chunki byłyby duplikatem treści w formie różnicowej: wyglądają jak przepis, cytują się
/// jak przepis i nie odpowiadają na żadne pytanie. To ta sama decyzja, którą projekt podjął dla
/// ISAP-u (<c>EliDiscoverOptions.Statuses</c> pomija „akt objęty tekstem jednolitym"), tylko 4 000 razy.
/// </summary>
public enum EuActClass
{
    /// <summary>Akt z własną treścią normatywną (nie zmienia i nie uchyla innych aktów) — 3 138 w populacji.
    /// Pełna ingestia: treść + chunki.</summary>
    Substantive,

    /// <summary>Akt zmieniający, którego zmiany są już wchłonięte w istniejącą wersję skonsolidowaną
    /// aktu bazowego — 3 662 w populacji. Metadane i relacje TAK, chunki NIE.</summary>
    AmendingAbsorbed,

    /// <summary>Akt zmieniający BEZ konsolidacji wchłaniającej (341 w populacji) — jedyne diffy, które
    /// realnie dokładają aktualnej treści. Ingestujemy treść, tak jak ISAP ingestuje nowele
    /// niewchłonięte do tekstu jednolitego.</summary>
    AmendingOpen,
}

/// <summary>
/// Klasyfikator klasy aktu — funkcja czysta, cała decyzja „chunkować czy nie" w jednym miejscu
/// i pokryta testami. Wejście: relacje aktu + konsolidacje, w których akt występuje.
/// </summary>
public static class EuActClassifier
{
    /// <param name="celex">CELEX klasyfikowanego aktu.</param>
    /// <param name="relations">Relacje aktu (co zmienia / co uchyla). Null = brak relacji.</param>
    /// <param name="absorbedBy">CELEX-y wersji skonsolidowanych, które wchłaniają ten akt (z relacji
    /// <c>act_consolidated_consolidates_resource_legal</c> patrzącej NA ten akt). Pusto = brak wchłonięcia.</param>
    public static EuActClass Classify(
        string celex, string? polishTitle, EuActRelations? relations, IReadOnlyCollection<string>? absorbedBy)
    {
        var rel = relations ?? EuActRelations.None;
        var amends = rel.Amends.Any(t => !IsSelf(t, celex));
        if (!amends) return EuActClass.Substantive;

        // Sama relacja „zmienia" NIE wystarcza — to poprawka po dwóch realnych pomiarach, z których
        // każdy obnażył grubszą regułę:
        // (1) „uchyla → bez treści" wyrzuciłoby RODO (uchyla dyrektywę 95/46/WE), GPSR i MDR;
        // (2) „zmienia + wchłonięte → bez treści" wyrzuciło w przebiegu bramkowym AI Act, DSA, DMA,
        //     REACH i MDR — bo akty MERYTORYCZNE też zmieniają inne akty w przepisach końcowych.
        // Rozstrzyga więc TYTUŁ: akt czysto nowelizujący ma imiesłów „zmieniające…" na pozycji
        // czasownika, przed jakimkolwiek własnym „w sprawie…". Zmierzone na populacji: 2 858 aktów
        // czysto nowelizujących, z czego 2 674 wchłoniętych w konsolidacje.
        if (!IsPureAmendment(polishTitle)) return EuActClass.Substantive;

        return absorbedBy is { Count: > 0 } ? EuActClass.AmendingAbsorbed : EuActClass.AmendingOpen;
    }

    /// <summary>
    /// Czy tytuł opisuje akt, którego CAŁYM przedmiotem jest zmiana innego aktu („Rozporządzenie …
    /// zmieniające rozporządzenie (WE) nr 396/2005 w odniesieniu do …"), a nie akt merytoryczny, który
    /// zmienia coś w przepisach końcowych („… w sprawie ustanowienia zharmonizowanych przepisów …
    /// oraz zmiany rozporządzeń …"). Kryterium pozycyjne, bo w tytule aktu nowelizującego „w sprawie"
    /// należy do tytułu aktu ZMIENIANEGO i występuje PO imiesłowie.
    /// Brak tytułu = zakładamy akt merytoryczny (lepiej wpuścić diff, niż zgubić RODO).
    /// </summary>
    public static bool IsPureAmendment(string? polishTitle)
    {
        if (string.IsNullOrWhiteSpace(polishTitle)) return false;

        var title = NormalizeWhitespace(polishTitle);
        var amendingAt = title.IndexOf("zmieniając", StringComparison.OrdinalIgnoreCase);
        if (amendingAt < 0) return false;

        var subjectAt = title.IndexOf("w sprawie", StringComparison.OrdinalIgnoreCase);
        return subjectAt < 0 || amendingAt < subjectAt;
    }

    /// <summary>
    /// Tytuły z CELLAR-a niosą TWARDE SPACJE (U+00A0) między słowami („w sprawie", „nr 396/2005").
    /// Bez tej normalizacji dopasowanie „w sprawie" nie trafia i akt merytoryczny zostaje uznany za nowelę
    /// — realnie zdarzyło się to REACH-owi i dyrektywie o prawach konsumentów w przebiegu bramkowym Fazy 1.
    /// </summary>
    public static string NormalizeWhitespace(string text) =>
        string.Join(' ', text
            .Replace(' ', ' ')   // twarda spacja — realnie w tytułach z CELLAR-a
            .Replace(' ', ' ')   // wąska twarda spacja
            .Replace(' ', ' ')   // spacja cienka
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Czy akt tej klasy wnosi treść do wektorów (chunki), czy zostaje przy metadanych.</summary>
    public static bool CarriesOwnText(this EuActClass cls) => cls is not EuActClass.AmendingAbsorbed;

    /// <summary>Wartość do metadanych dokumentu (`actClass`) — stabilny, czytelny klucz.</summary>
    public static string ToMetadataValue(this EuActClass cls) => cls switch
    {
        EuActClass.Substantive => "substantive",
        EuActClass.AmendingAbsorbed => "amending-absorbed",
        EuActClass.AmendingOpen => "amending-open",
        _ => "unknown",
    };

    /// <summary>Relacja do samego siebie (spotykana w danych) nie czyni z aktu noweli.</summary>
    private static bool IsSelf(string target, string celex) =>
        string.Equals(target, celex, StringComparison.OrdinalIgnoreCase);
}
