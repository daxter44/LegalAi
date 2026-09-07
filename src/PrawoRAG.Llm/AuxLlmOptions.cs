namespace PrawoRAG.Llm;

/// <summary>
/// Konfiguracja modelu POMOCNICZEGO (Zadanie 5 planu ROU) — lekki model 6–11 B do zadań służebnych:
/// router intencji (czy pytanie wymaga przepisów) i przeformułowanie zapytania na terminologię
/// ustawową dla drugiej rundy retrievalu. Model pomocniczy NIGDY nie pisze odpowiedzi dla
/// użytkownika i nigdy z nim nie rozmawia.
///
/// Dlaczego osobny model, a nie ten sam co odpowiadający: pomiar <c>PRAWORAG_LOG_TIMING</c> pokazał
/// ~41 s rozumowania na jedną odpowiedź. Dokładanie takiego wywołania do KAŻDEGO pytania (żeby
/// tylko rozstrzygnąć „czy to small-talk") byłoby absurdem — router potrzebuje jednej decyzji,
/// nie rozumowania.
/// </summary>
public sealed class AuxLlmOptions
{
    public const string SectionName = "Llm:Aux";

    /// <summary>Bazowy URL API zgodnego z OpenAI. Może być ten sam serwer co model główny (inny model)
    /// albo osobny — np. Bielik lokalnie w Ollamie obok Gemmy przez Sherlock AI.</summary>
    public string BaseUrl { get; set; } = "http://localhost:11434/v1";

    /// <summary>Nazwa modelu znana serwerowi. Domyślnie Bielik 11B (jak <see cref="LocalLlmOptions"/>) —
    /// wybór finalny należy podjąć evalem (testy routera z Zadania 8), nie preferencją.</summary>
    public string Model { get; set; } = "SpeakLeash/bielik-11b-v3.0-instruct:Q5_K_M";

    public string? ApiKey { get; set; }

    /// <summary>
    /// Twardy limit odpowiedzi. Zadania pomocnicze zwracają krótki JSON albo jedno zapytanie, więc
    /// niski limit jest jednocześnie zabezpieczeniem: nawet gdy serwer nie pozwala wyłączyć
    /// „myślenia" per żądanie, ucina je siłowo (reguła R2 planu — rozumowanie tylko przy PISANIU
    /// odpowiedzi).
    /// </summary>
    public int MaxTokens { get; set; } = 256;

    /// <summary>
    /// Timeout SKOŃCZONY i krótki — świadoma różnica względem klienta modelu głównego, który ma
    /// <c>Timeout.InfiniteTimeSpan</c> (długa generacja to tam normalny przypadek). Tu odwrotnie:
    /// model pomocniczy ma być szybki albo żaden. Każda awaria/timeout kończy się fallbackiem
    /// w stronę retrievalu, więc krótki timeout to degradacja, nie błąd.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Parametr OpenAI-compat <c>reasoning_effort</c>. Domyślnie <c>null</c> (NIE wysyłany) — świadomie,
    /// po dwóch sprzecznych pomiarach 2026-08-24 na tym samym dniu:
    /// 1. Bez tego parametru: model, który całą odpowiedź oznacza jako „myślenie" (flaga
    ///    google.thought), potrafi wyczerpać <see cref="MaxTokens"/> ZANIM wyemituje choć jeden znak
    ///    widocznej treści — <c>router.raw</c> wychodzi pusty, router fail-safe'uje do retrievalu przy
    ///    KAŻDYM pytaniu, po cichu, bez błędu.
    /// 2. Z tym parametrem ustawionym na <c>"none"</c>, dla modelu <c>gemma-4-26b-a4b-it</c> przez
    ///    <c>generativelanguage.googleapis.com</c>: Gemini API zwraca HTTP 400 <c>INVALID_ARGUMENT</c>
    ///    „Thinking budget is not supported for this model" — czyli NIE każdy model z rodziny
    ///    Gemini/Gemma na tym endpoincie go honoruje; dla niektórych to twardy błąd, nie cichy no-op.
    /// Wniosek: to pole trzeba ustawiać PER MODEL, nie zakładać uniwersalnego bezpiecznego defaultu.
    /// Dla modeli, które faktycznie wspierają <c>"none"</c> (część klasy Gemini 2.5 — potwierdź
    /// w dokumentacji swojego modelu), ustaw jawnie przez <c>Llm__Aux__ReasoningEffort=none</c>. Jeśli
    /// model w ogóle nie daje się wyłączyć z myślenia, jedyne dźwignie to: podniesienie
    /// <see cref="MaxTokens"/> (kosztem latencji/ceny Aux — wraca to, co miał oszczędzać) albo zmiana
    /// modelu Aux na lżejszy/nie-rozumujący (patrz komentarz klasy — stąd był domyślnie lokalny Bielik).
    /// </summary>
    public string? ReasoningEffort { get; set; }
}
