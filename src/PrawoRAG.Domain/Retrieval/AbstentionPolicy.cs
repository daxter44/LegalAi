namespace PrawoRAG.Domain.Retrieval;

/// <summary>
/// Bramka abstynencji — rdzeń wartości produktu. Gdy retrieval nie ma wystarczającego pokrycia,
/// system NIE generuje odpowiedzi, tylko mówi „nie mam wystarczających źródeł" (zamiast halucynować).
/// </summary>
public static class AbstentionPolicy
{
    /// <summary>Domyślny próg podobieństwa cosine (kalibrowany na golden secie — zadanie 5.3).</summary>
    public const double DefaultThreshold = 0.55;

    /// <summary>
    /// Odmawiamy, gdy nie ma żadnych kandydatów ALBO gdy najlepsze podobieństwo cosine nie sięga progu.
    ///
    /// WYJĄTEK: trafienie DOKŁADNE (<see cref="RetrievalResult.ExactMatchHits"/>) przepuszcza bramkę
    /// niezależnie od cosine. Powód jest pomiarowy, nie estetyczny: sygnatura akt i numer Dziennika
    /// Ustaw to IDENTYFIKATORY, nie zapytania semantyczne — goła sygnatura („III SA/Po 154/26")
    /// embeduje się bezwartościowo, więc `MaxSimilarity` bywa poniżej progu DOKŁADNIE wtedy, gdy
    /// w kontekście leży orzeczenie wprost wskazane przez użytkownika. Bramka i tory exact-match
    /// unieważniały się nawzajem: zbudowano je na ten przypadek, a próg je kasował.
    ///
    /// Czego ten wyjątek NIE robi: nie zmienia skali ani semantyki <see cref="RetrievalResult.MaxSimilarity"/>
    /// (próg 0,55 zostaje kalibrowany na tym samym sygnale co dotąd) i nie obejmuje mostu cytowań —
    /// most jest sygnałem pochodnym, nie jawnym askiem. Ryzyko rezydualne: fałszywe rozpoznanie cytatu
    /// przepuści pytanie bez pokrycia — łapie to druga linia obrony, reguła 3 promptu (odmowa NA POZIOMIE
    /// TREŚCI, gdy dostarczone źródła nie odpowiadają na pytanie).
    /// </summary>
    public static bool ShouldAbstain(RetrievalResult result, double threshold = DefaultThreshold) =>
        result.Chunks.Count == 0 || (result.ExactMatchHits == 0 && result.MaxSimilarity < threshold);

    public const string Message =
        "Nie mam wystarczających źródeł, aby odpowiedzieć. Zawęź pytanie lub wskaż konkretny akt/sygnaturę.";
}
