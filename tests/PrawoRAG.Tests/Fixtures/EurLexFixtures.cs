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

    /// <summary>RODO — tekst BAZOWY z Dz.U. UE (markup „oj-*": p.oj-ti-art, litery w tabelach 4%/96%).</summary>
    public const string RodoBase = "rodo_32016R0679.base.xhtml";

    /// <summary>RODO — tekst SKONSOLIDOWANY (markup „*-norm": p.title-article-norm, grid-list).</summary>
    public const string RodoConsolidated = "rodo_02016R0679-20160504.cons.xhtml";

    /// <summary>AI Act — wycinek tekstu skonsolidowanego: art. 5 (ze znacznikami wersji „▼M1"),
    /// art. 75a (sufiks literowy z noweli) i ZAŁĄCZNIK III (wykaz systemów wysokiego ryzyka).</summary>
    public const string AiActSlice = "ai_act_02024R1689-20260727.slice.xhtml";

    /// <summary>e-Privacy — konsolidacja z 2009 r. w markupie LEGACY: converter 7.6.2, ZERO kotwic
    /// id="art_*". To jedyna polska wersja tego aktu, więc bez toru tekstowego nie ma go w korpusie.</summary>
    public const string EPrivacyLegacy = "eprivacy_02002L0058-20091219.legacy.xhtml";

    /// <summary>Jedna strona odkrywania zakresu (25 CELEX-ów z 2024 r.).</summary>
    public const string DiscoverPage = "sparql_discover_page.json";

    /// <summary>Pusta strona wyników — koniec stronicowania.</summary>
    public const string EmptyPage = "sparql_empty_page.json";

    public static string Read(string fileName) => File.ReadAllText(Path.Combine(Dir, fileName));
}
