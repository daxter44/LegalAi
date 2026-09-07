namespace PrawoRAG.Api.Services.Plans;

/// <summary>
/// Słownik planów (E1/T-8). Konfiguracja, nie tabela — dwa plany nie potrzebują CRUD-u, a zmiana
/// limitu ma być zmianą ustawienia, nie migracją.
///
/// UWAGA na dwie różne osie limitów, których nie wolno pomieszać:
/// • <b>plan</b> (tutaj) — ile MIESIĘCZNIE wolno użytkownikowi; to jest oś rozliczeniowa;
/// • <b>globalne capy dzienne</b> (<see cref="AccessOptions"/>) — ile łącznie zniesie nasza pojemność
///   w ciągu doby; to jest oś sprzętowa i zostaje niezależnie od tego, kto ile zapłacił.
/// Bez tej drugiej jeden klient z planem płatnym potrafiłby położyć serwer w kilka godzin.
/// </summary>
public sealed class PlanOptions
{
    public const string SectionName = "Plans";

    /// <summary>Plan nadawany przy rejestracji.</summary>
    public string DefaultPlan { get; set; } = PlanIds.Free;

    /// <summary>Plan → limity. Klucze zgodne z <see cref="PlanIds"/>.</summary>
    public Dictionary<string, PlanLimits> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        // AnalysesPerMonth/MaxUnitsPerAnalysis: wartości ROBOCZE do decyzji cenowej (2026-09-02) —
        // zmiana to zmiana konfiguracji (Plans:Items:{plan}:AnalysesPerMonth), nie kodu.
        [PlanIds.Free] = new() { DisplayName = "Darmowy", RequestsPerMonth = 15, AnalysesPerMonth = 3, MaxUnitsPerAnalysis = 20 },
        [PlanIds.Pro] = new() { DisplayName = "Pro", RequestsPerMonth = 300, AnalysesPerMonth = 50, MaxUnitsPerAnalysis = 40 },
    };

    /// <summary>
    /// Limity planu; gdy w konfiguracji brakuje wpisu, wraca plan darmowy — nieznana nazwa nie może
    /// oznaczać „bez limitu".
    /// </summary>
    public PlanLimits Resolve(string? planId) =>
        planId is not null && Items.TryGetValue(planId, out var found) ? found
        : Items.TryGetValue(DefaultPlan, out var fallback) ? fallback
        : new PlanLimits { DisplayName = "Darmowy", RequestsPerMonth = 15 };
}

/// <summary>Nazwy planów w jednym miejscu — te same stringi lądują w bazie i w konfiguracji.</summary>
public static class PlanIds
{
    public const string Free = "free";
    public const string Pro = "pro";
}

/// <summary>Co wolno w danym planie. Kolejne limity dokładają się tutaj.</summary>
public sealed class PlanLimits
{
    /// <summary>Nazwa pokazywana człowiekowi.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Zapytania do LLM na okres rozliczeniowy — CZAT. Analiza dokumentów ma od 2026-09-02
    /// OSOBNĄ pulę (<see cref="AnalysesPerMonth"/>): wcześniej liczyła się per fragment do tej puli,
    /// więc jeden 19-fragmentowy dokument zjadał cały miesięczny limit darmowy.</summary>
    public int RequestsPerMonth { get; set; }

    /// <summary>Analizy dokumentów na okres rozliczeniowy — naliczane PER DOKUMENT (start analizy),
    /// nie per fragment; ponowienie nieudanych fragmentów jest wliczone w already-opłaconą analizę.</summary>
    public int AnalysesPerMonth { get; set; }

    /// <summary>Cap fragmentów jednego dokumentu w tym planie (nadmiar ucinany z jawną flagą, jak
    /// globalny <c>Analysis:MaxUnits</c>) — bez niego pula analiz nie ogranicza kosztu infrastruktury,
    /// bo „1 analiza" mogłaby znaczyć od 3 do 40 wywołań LLM.</summary>
    public int MaxUnitsPerAnalysis { get; set; } = 40;
}
