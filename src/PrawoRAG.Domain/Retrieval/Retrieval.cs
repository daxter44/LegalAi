using PrawoRAG.Domain.Documents;

namespace PrawoRAG.Domain.Retrieval;

/// <summary>
/// Etap retrievalu raportowany na bieżąco do UI (Zadanie 2 planu ROU). Raportowany PRZED wykonaniem
/// etapu — użytkownik ma widzieć, co się ZACZYNA, nie co się skończyło.
/// </summary>
/// <param name="Name">Techniczna nazwa etapu — TA SAMA co w <see cref="LatencyLog"/>
/// (<c>embed</c>, <c>dense</c>, <c>rerank.main</c>…), żeby instrumentacja i UI nie mogły się rozjechać.</param>
/// <param name="Label">Etykieta dla użytkownika, po polsku.</param>
/// <param name="Count">Liczba, jeśli znana (kandydaci, chunki) — liczba buduje zaufanie do czekania.</param>
public sealed record RetrievalStage(string Name, string Label, int? Count = null);

/// <summary>Zapytanie do retrievera: tekst + filtry metadanych + parametry top-K.</summary>
public sealed record RetrievalQuery
{
    public required string Text { get; init; }

    /// <summary>
    /// Tekst dla torów DOKŁADNYCH (sygnatura orzeczenia, numer Dziennika Ustaw, cytat artykułu) —
    /// gdy różni się od <see cref="Text"/>. Null = użyj <see cref="Text"/> (domyślne, zero zmian dla
    /// zapytań bez follow-upu). Rozdzielenie istnieje, bo przy dopytaniach <see cref="Text"/> niesie
    /// fold z POPRZEDNIEJ ODPOWIEDZI (kotwice źródeł, cytaty, fragment) — wzbogacenie SEMANTYCZNE pod
    /// anaforę. Ale sygnatura/numer/artykuł wyłuskany z ODPOWIEDZI systemu to źródło, którego user nie
    /// wpisał — nie może wyzwalać exact-match (bug: kotwice trafionych wyroków zalewały cały TopK
    /// „dokładnym" trafieniem w orzeczenie, o które nikt nie pytał). Tor gęsty/BM25 dalej czyta pełny
    /// <see cref="Text"/>; tory dokładne — <see cref="EffectiveExactMatchText"/>.
    /// </summary>
    public string? ExactMatchText { get; init; }

    /// <summary>Tekst faktycznie zasilający tory dokładne: <see cref="ExactMatchText"/> jeśli podany,
    /// inaczej <see cref="Text"/> (kompatybilność wsteczna — /api/search, testy, pytania bez historii).</summary>
    public string EffectiveExactMatchText => ExactMatchText ?? Text;

    /// <summary>
    /// Tekst, którym cross-encoder ocenia kandydatów — gdy różni się od <see cref="Text"/>. Null =
    /// użyj <see cref="Text"/> (domyślne: /api/search, pytania bez historii, testy). Rozdzielenie
    /// istnieje z tego samego powodu co <see cref="ExactMatchText"/>, tylko po stronie SĘDZIEGO:
    /// przy follow-upie <see cref="Text"/> niesie fold z POPRZEDNIEJ ODPOWIEDZI, więc reranker
    /// dostawał do oceny tekst, którego spory kawałek sam był ocenianą treścią — sklejka oceniała
    /// samą siebie i wygrywała mimo gorszych źródeł (zmierzone 2026-08-11: fold 0.8576 cosine przy
    /// 0/8 trafnych slotów vs surowe 0.8431 przy 5/8). Tor gęsty/BM25 dalej czyta pełny
    /// <see cref="Text"/> — wzbogacenie semantyczne pod anaforę zostaje nietknięte.
    /// </summary>
    public string? RerankText { get; init; }

    /// <summary>Tekst faktycznie zasilający cross-encoder: <see cref="RerankText"/> jeśli podany,
    /// inaczej <see cref="Text"/>.</summary>
    public string EffectiveRerankText => RerankText ?? Text;

    /// <summary>Liczba finalnych kandydatów po fuzji RRF (kontekst dla LLM).</summary>
    public int TopK { get; init; } = 8;

    /// <summary>Liczba kandydatów z każdej ścieżki (dense, BM25) przed fuzją.</summary>
    public int CandidatesPerPath { get; init; } = 50;

    // --- filtry metadanych ---
    public string? CourtType { get; init; }
    public DateOnly? DateFrom { get; init; }
    public DateOnly? DateTo { get; init; }
    public bool OnlyInForce { get; init; }

    /// <summary>
    /// Minimalna liczba tokenów chunka (0 = bez filtra). Zdegenerowane mini-chunki („⚫", „(",
    /// pojedyncze linie formularzy) mają wysokie cosine do KAŻDEGO zapytania i zaśmiecają top-K.
    /// </summary>
    public int MinChunkTokens { get; init; }

    /// <summary>
    /// Most cytowań: maksymalna liczba artykułów dociąganych z cytowań w trafionych orzeczeniach
    /// (0 = wyłączony). Diagnoza 2026-07-17: przepis rządzący (art. 415 KC) jest nieretrievalny dla
    /// pytań opisowych — ale trafione orzeczenia same go cytują; sonda --probe-akty potwierdziła
    /// (415: 3 niezależne dokumenty w top-30; act-only lane obalony — wygrywała pułapka art. 149).
    /// </summary>
    public int CitationBridgeArticles { get; init; } = 2;

    /// <summary>
    /// Ile artykułów w KAŻDĄ stronę dociągnąć wokół trafień w dominującym akcie (plan SAS).
    /// 0 = mechanizm wyłączony, wynik bajt w bajt jak przed jego wprowadzeniem — ten sam idiom co
    /// <see cref="CitationBridgeArticles"/>.
    ///
    /// Powód: retrieval potrafi trafić w AKT i jednocześnie ominąć właściwy przepis, bo ten nazywa
    /// się inaczej niż w pytaniu (zmierzone: pytanie o „limity wpłat", ustawa mówi „próg zwolnienia";
    /// 8 z 8 źródeł z właściwej ustawy, ani jedno z limitem). W tekstach prawnych progi i wyjątki
    /// leżą FIZYCZNIE OBOK przepisu, który modyfikują — więc sąsiedztwo omija problem terminologii,
    /// nie wiedząc nic o terminologii.
    /// </summary>
    public int NeighbourhoodRadius { get; init; }

    /// <summary>Ile chunków z jednego aktu w finalnej liście kwalifikuje go do rozszerzenia.
    /// Ogranicza zasięg zmiany: pytania z rozproszonymi źródłami zachowują się jak dotąd.</summary>
    public int NeighbourhoodMinChunks { get; init; } = 3;

    /// <summary>
    /// Górny limit tokenów DOCIĄGNIĘTYCH chunków. To cała obsługa przypadku „kodeks" — bez osobnej
    /// gałęzi: dla 18-stronicowej ustawy budżet obejmuje w praktyce cały akt, dla kodeksu cywilnego
    /// ucina do okolic trafień. Liczone z <c>ChunkEntity.TokenCount</c>, więc bez tokenizacji.
    /// </summary>
    public int NeighbourhoodTokenBudget { get; init; } = 20_000;

    /// <summary>
    /// Most vacatio legis: ile chunków dociągnąć z jednostek WSKAZANYCH w klauzuli wejścia w życie,
    /// gdy taka klauzula trafiła do wyniku (0 = wyłączony, wynik bajt w bajt jak przed zmianą — ten sam
    /// idiom co <see cref="CitationBridgeArticles"/> i <see cref="NeighbourhoodRadius"/>).
    ///
    /// Powód (DIAGNOZA-NOWELIZACJA-DATA-WEJSCIA-W-ZYCIE-2026-08-27): pytanie „jakie zmiany wejdą w życie
    /// we wrześniu 2026" trafia w klauzulę („z dniem 20 września 2026 r. wchodzą w życie art. 1 pkt 1
    /// lit. a i c oraz pkt 3"), ale treść tych przepisów ma przy DOKŁADNYM skanie rangi #2367/#50430/#82405
    /// — trzy rzędy wielkości od okna kandydatów, więc nie da się tego naprawić ani progiem, ani HNSW.
    /// Pytanie niesie datę, treść nowelizacji nie niesie żadnej daty, a łącznik między nimi to CYTOWANIE
    /// wewnątrz dokumentu. Dlatego dociągamy strukturalnie, tak jak most cytowań dociąga przepis
    /// z orzeczenia — z pominięciem embeddingu, bo nie ma tu czego mierzyć podobieństwem.
    ///
    /// Sąsiedztwo (<see cref="NeighbourhoodRadius"/>) tego NIE łapie: jest pozycyjne, więc wokół art. 13
    /// dociągnie art. 12 i 14, a treść nowelizacji siedzi w art. 1.
    /// </summary>
    public int VacatioLegisChunks { get; init; } = 8;

    /// <summary>
    /// Raportowanie etapów na bieżąco (Zadanie 2 planu ROU) — ten sam wzorzec opcjonalnego
    /// wzbogacenia zapytania co <see cref="RerankText"/>/<see cref="ExactMatchText"/>.
    /// Null = nikt nie słucha (Eval, <c>/api/search</c>, testy) i retrieval zachowuje się bajt
    /// w bajt jak dotąd. Powód: pytanie prawne trwa ~85 s, a UI nie miało czym pokazać, że pracuje.
    /// </summary>
    public IProgress<RetrievalStage>? Progress { get; init; }

    /// <summary>Prefiks etykiet etapów — przy follow-upie retrieval leci DWA razy (wariant surowy
    /// i kontekstowy), więc bez rozróżnienia UI pokazuje te same etapy dwukrotnie bez wyjaśnienia,
    /// dlaczego odpowiedź trwa dwa razy dłużej. Null = bez prefiksu (pojedynczy przebieg).</summary>
    public string? ProgressLabelPrefix { get; init; }

    /// <summary>Raportuje etap, jeśli ktoś słucha. Bezpieczne przy null (zero kosztu).</summary>
    public void ReportStage(string name, string label, int? count = null) =>
        Progress?.Report(new RetrievalStage(
            name, ProgressLabelPrefix is { Length: > 0 } p ? $"{p}{label}" : label, count));
}

/// <summary>Pojedynczy trafiony chunk z lokalizatorem cytatu i wynikiem.</summary>
public sealed record RetrievedChunk
{
    public Guid ChunkId { get; init; }
    public Guid DocumentId { get; init; }

    /// <summary>
    /// Pozycja chunka w dokumencie (od 0). Potrzebna, żeby dociągnąć SĄSIEDNIE artykuły
    /// (<see cref="ArticleNeighbourhood"/>) i żeby akt czytał się w prompcie liniowo — a nie
    /// w kolejności podobieństwa. Baza ma pod to unikalny indeks <c>(DocumentId, ChunkIndex)</c>.
    /// </summary>
    public int ChunkIndex { get; init; }
    public required string Text { get; init; }
    public string? Section { get; init; }
    public required string Source { get; init; }
    public required string DocType { get; init; }
    public required string Title { get; init; }
    public string? SourceUrl { get; init; }
    public CitationLocator? Locator { get; init; }

    /// <summary>Wynik fuzji RRF (im wyżej, tym lepiej).</summary>
    public double Score { get; init; }

    /// <summary>Podobieństwo cosine (1 − dystans) z toru gęstego, jeśli chunk był w nim obecny.</summary>
    public double? Similarity { get; init; }

    /// <summary>Score rerankera (cross-encoder), jeśli reranking był włączony. Null = bez rerankingu.</summary>
    public double? RerankScore { get; init; }

    /// <summary>AKT-4: data wejścia w życie, gdy chunk to fragment nowelizacji NIEWCHŁONIĘTEJ do tekstu
    /// jednolitego (dołożony przez <see cref="ITemporalAugmenter"/>). Null = zwykłe źródło.</summary>
    public string? AmendmentEffectiveDate { get; init; }

    /// <summary>Podstawy prawne, na których oparło się orzeczenie (z metadanych dokumentu:
    /// <c>referencedRegulations</c>). Konkretna informacja dla prawnika — pokazywana jako chipy przy
    /// karcie wyroku, bez czytania uzasadnienia. Null/pusto dla aktów i orzeczeń bez tych metadanych.</summary>
    public IReadOnlyList<string>? LegalBases { get; init; }
}

/// <summary>
/// Wynik retrievalu + DWA rozdzielone sygnały (kalibracja przed pilotażem, znalezisko z raportu
/// odmów 2026-07-20): <see cref="MaxSimilarity"/> to ZAWSZE cosine z toru gęstego (stabilna skala,
/// porównywalna między biegami — na niej stoi bramka abstynencji i diagnostyka), a
/// <see cref="RerankTopScore"/> to top-1 cross-encodera (świetny do RANKINGU źródeł, ale odpowiada
/// na inne pytanie: „które z PODANYCH najlepsze", nie „czy wystarcza" — klastruje ~0,99 nawet na
/// śmieciowej puli). Wcześniej reranker po cichu NADPISYWAŁ MaxSimilarity swoim score — próg
/// kalibrowany pod cosine przestawał cokolwiek znaczyć. Null = reranker wyłączony/pusto.
/// </summary>
/// <param name="ExactMatchHits">
/// Ile chunków w <see cref="Chunks"/> przyszło torem DOKŁADNYM na podstawie tego, co użytkownik
/// NAPISAŁ: sygnatura akt („III SA/Po 154/26"), numer Dziennika Ustaw („Dz.U. 2025 poz. 1815") albo
/// cytat artykułu („art. 415 KC"). Trzeci rozdzielony sygnał, z tego samego powodu co
/// <see cref="RerankTopScore"/>: bramka abstynencji stoi na cosine z toru gęstego, a trafienie
/// dokładne jest odpowiedzią na INNE pytanie — nie „jak blisko semantycznie", ale „czy mamy DOKŁADNIE
/// ten dokument, o który pytano". Goła sygnatura embeduje się bezwartościowo (nie jest zapytaniem
/// semantycznym, a identyfikatorem), więc bez tego sygnału system odmawiał, TRZYMAJĄC w kontekście
/// dokument wprost wskazany przez użytkownika — czyli tory sygnatury/aktu/cytatu unieważniały się
/// nawzajem z bramką.
///
/// Świadomie NIE liczy tu mostu cytowań: most to sygnał POCHODNY (przepis wygłosowany z cytowań
/// w trafionych orzeczeniach), nie jawny ask użytkownika — przepuszczanie bramki na jego podstawie
/// rozluźniałoby ją dla pytań opisowych, gdzie próg cosine jest jedyną obroną przed odpowiadaniem
/// na podstawie przypadkowej puli.
/// </param>
public sealed record RetrievalResult(
    IReadOnlyList<RetrievedChunk> Chunks, double MaxSimilarity, double? RerankTopScore = null,
    int ExactMatchHits = 0);

public interface IRetriever
{
    Task<RetrievalResult> RetrieveAsync(RetrievalQuery query, CancellationToken ct);
}

/// <summary>
/// Aktualność (AKT-2/4b): zwraca CAŁĄ (zastępczą) listę chunków — oryginalne wejście, z których część może
/// być OZNACZONA (<see cref="RetrievedChunk.AmendmentEffectiveDate"/> ustawione), gdy jej własny dokument
/// jest niewchłoniętą nowelą (trafiła zwykłym retrievalem, nie przez dopasowanie cytatu) — plus DOŁOŻONE
/// nowe fragmenty nowel dotyczące pytanych artykułów. Nigdy nie USUWA istniejących wyników. Gdy nie ma
/// żadnych świeżych nowel do oznaczenia/dołożenia, zwraca <paramref name="retrieved"/> bez zmian.
/// Caller PODMIENIA wynikiem, nie dokleja (kontrakt inny niż „tylko dołożenia").
/// </summary>
public interface ITemporalAugmenter
{
    Task<IReadOnlyList<RetrievedChunk>> AugmentAsync(
        RetrievalQuery query, IReadOnlyList<RetrievedChunk> retrieved, CancellationToken ct);
}
