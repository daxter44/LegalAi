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
}
