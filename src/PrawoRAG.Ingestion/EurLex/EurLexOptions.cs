namespace PrawoRAG.Ingestion.EurLex;

/// <summary>
/// Konfiguracja źródła EUR-Lex/CELLAR. Zakres definiuje ODKRYWANIE (<see cref="Discover"/>) —
/// „prawo UE" to obowiązujące rozporządzenia i dyrektywy z polskim tekstem (zmierzone: 7 756 aktów,
/// z czego ~6 750 ma wersję polską), a nie ręczna lista. <see cref="Acts"/> steruje tylko KOLEJNOŚCIĄ
/// (akty z zestawu pomiarowego idą w pierwszej transzy) i pozwala dołożyć akt spoza filtra.
/// </summary>
public sealed class EurLexOptions
{
    public const string SectionName = "EurLex";

    /// <summary>Baza CELLAR-a do pobierania treści po CELEX-ie (negocjacja zawartości nagłówkiem Accept).</summary>
    public string BaseUrl { get; set; } = "https://publications.europa.eu/resource/celex";

    /// <summary>Endpoint SPARQL CELLAR-a — odkrywanie zakresu, relacje i wersje skonsolidowane.</summary>
    public string SparqlUrl { get; set; } = "https://publications.europa.eu/webapi/rdf/sparql";

    /// <summary>Kod języka treści (3-literowy, jak w authority-table CELLAR-a). Korpus jest polski.</summary>
    public string Language { get; set; } = "pol";

    /// <summary>CELEX-y brane PIERWSZE, przed odkrytymi (np. „32016R0679"). Lista otwarta.</summary>
    public List<string> Acts { get; set; } = [];

    /// <summary>Limit na pojedynczą próbę HTTP (s) — akty UE bywają duże (REACH skonsolidowany ~5,4 MB),
    /// a zapytania SPARQL o cały zakres liczą się w minutach.</summary>
    public int AttemptTimeoutSeconds { get; set; } = 45;

    /// <summary>
    /// Przerwa między żądaniami do CELLAR-a (ms). Kilka tysięcy pobrań bez przerwy to prosta droga
    /// do odcięcia po IP w środku transzy — magazyn surowych pozwala wznowić, ale ban kosztuje przebieg.
    /// </summary>
    public int RequestDelayMs { get; set; } = 250;

    /// <summary>Ile CELEX-ów w jednym zbiorczym zapytaniu SPARQL (<c>VALUES</c>). Pytanie per akt to
    /// 7 756 zapytań; zbiorcze schodzi do ~150 na kategorię.</summary>
    public int BatchSize { get; set; } = 50;

    public EurLexDiscoverOptions Discover { get; set; } = new();
}

/// <summary>
/// Odkrywanie zakresu przez SPARQL: typ zasobu + „obowiązujący" + rocznik. Analogia do
/// <c>EliDiscoverOptions</c> dla ISAP-u. Domyślnie rozporządzenia i dyrektywy — akty delegowane
/// i wykonawcze (REG_DEL, REG_IMPL, DIR_DEL, DIR_IMPL: +9 711 obowiązujących) są POZA domyślnym
/// zakresem, bo to w większości techniczne załączniki (taryfy, wykazy, wzory), które rozmyłyby
/// retrieval; dokłada się je świadomie, wpisując typ do <see cref="ResourceTypes"/>.
/// </summary>
public sealed class EurLexDiscoverOptions
{
    public bool Enabled { get; set; }

    /// <summary>Typy zasobu z authority-table CELLAR-a: „REG", „DIR", „REG_IMPL", „REG_DEL", „DIR_IMPL", „DIR_DEL".</summary>
    public List<string> ResourceTypes { get; set; } = ["REG", "DIR"];

    /// <summary>Tylko akty obowiązujące (<c>cdm:resource_legal_in-force</c>). Uchylone to nie prawo aktualne.</summary>
    public bool InForceOnly { get; set; } = true;

    /// <summary>Rocznik od (rok z CELEX-u). 1958 = początek dorobku prawnego.</summary>
    public int YearFrom { get; set; } = 1958;

    public int YearTo { get; set; } = 2100;

    /// <summary>Rozmiar strony SPARQL-a. Endpoint zwraca 500 przy zbyt dużym OFFSET-cie (zmierzone przy
    /// 9000), więc stronicowanie kończy PUSTA strona albo błąd — oba znaczą „koniec listy".</summary>
    public int PageSize { get; set; } = 3000;
}
