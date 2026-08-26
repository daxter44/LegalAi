namespace PrawoRAG.Tests.Fixtures;

/// <summary>
/// REALNE odpowiedzi endpointu SPARQL CELLAR-a zapisane 2026-08-26 (Fixtures/EurLex). Testy zakresu
/// i klasyfikacji aktów UE działają offline na tym, co serwer naprawdę zwraca — bo wszystkie pułapki
/// (konsolidacje obcych aktów, wersje z przyszłą datą, gYear w filtrze rocznika) są własnością danych,
/// nie naszego kodu, i na wymyślonym JSON-ie nie byłyby widoczne.
/// </summary>
public static class EurLexFixtures
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "EurLex");

    /// <summary>Konsolidacje dla RODO, AI Act i REACH — zawiera też konsolidacje OBCYCH aktów
    /// (np. „01995L0046-20180525" pod RODO), więc dowodzi filtra po prefiksie CELEX-u.</summary>
    public const string Consolidations = "sparql_consolidations.json";

    /// <summary>Relacje: akt zmieniający (32018R0070 → zmienia 32005R0396), akt bez relacji (RODO),
    /// akt uchylający (32005R0080).</summary>
    public const string Relations = "sparql_relations.json";

    /// <summary>Polskie tytuły czterech aktów: RODO i AI Act (merytoryczne, „w sprawie…" przed zmianami),
    /// 32018R0070 (czysto nowelizujące) i 32005R0080 (uchylające). To na tytule stoi klasyfikacja.</summary>
    public const string Titles = "sparql_titles.json";

    /// <summary>Jedna strona odkrywania zakresu (25 CELEX-ów z 2024 r.).</summary>
    public const string DiscoverPage = "sparql_discover_page.json";

    /// <summary>Pusta strona wyników — koniec stronicowania.</summary>
    public const string EmptyPage = "sparql_empty_page.json";

    public static string Read(string fileName) => File.ReadAllText(Path.Combine(Dir, fileName));
}
