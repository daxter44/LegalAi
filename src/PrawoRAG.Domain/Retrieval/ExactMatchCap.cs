namespace PrawoRAG.Domain.Retrieval;

/// <summary>
/// Limit dominacji jednego dokumentu w torach DOKŁADNYCH (exact-match: sygnatura, numer Dziennika
/// Ustaw, cytat artykułu). Tory te dociągają po kilkanaście chunków JEDNEGO dokumentu ze
/// <c>Score = double.MaxValue</c> — przy małym <c>TopK</c> jedno trafienie potrafi zająć CAŁY budżet
/// źródeł, wypychając kontekst (orzecznictwo obok ustawy, inny cytowany akt). Zostawiamy trafieniu
/// przewagę (jawny ask użytkownika), ale rezerwujemy kilka slotów, żeby jeden dokument nie zjadł
/// wszystkich. Czysta funkcja — testowalna bez DB.
/// </summary>
public static class ExactMatchCap
{
    /// <summary>Ile slotów TopK zostaje zarezerwowanych POZA torami dokładnymi, żeby jeden trafiony
    /// dokument nie mógł zająć całego budżetu (kontekst semantyczny/most cytowań wchodzi zawsze).</summary>
    public const int ReservedSlots = 2;

    /// <summary>Górny limit chunków JEDNEGO dokumentu w połączonej liście torów dokładnych, wyliczony
    /// z <paramref name="topK"/> i <see cref="ReservedSlots"/> (min. 1). Dla TopK=8 → 6.</summary>
    public static int MaxPerDocument(int topK) => Math.Max(1, topK - ReservedSlots);

    /// <summary>Zachowuje kolejność wejścia, ale odcina chunki po przekroczeniu <paramref name="maxPerDoc"/>
    /// dla danego <see cref="RetrievedChunk.DocumentId"/>. Chunki tego samego dokumentu poza limitem
    /// są pomijane (nie przesuwane) — wcześniejsze (wyżej scorowane) wygrywają slot.</summary>
    public static List<RetrievedChunk> LimitPerDocument(IEnumerable<RetrievedChunk> chunks, int maxPerDoc)
    {
        var perDoc = new Dictionary<Guid, int>();
        var result = new List<RetrievedChunk>();
        foreach (var c in chunks)
        {
            var n = perDoc.GetValueOrDefault(c.DocumentId);
            if (n >= maxPerDoc) continue;
            perDoc[c.DocumentId] = n + 1;
            result.Add(c);
        }
        return result;
    }
}
