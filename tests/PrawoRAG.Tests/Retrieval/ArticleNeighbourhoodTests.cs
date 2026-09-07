using PrawoRAG.Domain;
using PrawoRAG.Domain.Retrieval;

namespace PrawoRAG.Tests.Retrieval;

/// <summary>
/// T-NEIGH (Zadanie 1 planu SAS) — plan zakresów sąsiedztwa dla dominującego aktu.
///
/// Przypadek źródłowy: pytanie o limity wpłat na OKI. 8 z 8 źródeł przyszło z właściwej ustawy, ale
/// żadne nie zawierało limitu — bo w ustawie nazywa się on inaczej („próg zwolnienia"). Pominięcie
/// było SYSTEMATYCZNE: kryterium wyboru jest ślepe na synonim ustawowy. Naprawa opiera się na tym,
/// że w tekstach prawnych progi i wyjątki leżą FIZYCZNIE OBOK przepisu, który modyfikują.
///
/// Świadomie bez metryki „pewności identyfikacji aktu": nie identyfikujemy aktu, tylko rozszerzamy
/// to, co retrieval już wybrał. Koncentracja wyników JEST tym sygnałem (w OKI: 8/8).
/// </summary>
public class ArticleNeighbourhoodTests
{
    private static RetrievedChunk Chunk(Guid docId, int chunkIndex, string docType = DocTypes.Act) => new()
    {
        ChunkId = Guid.CreateVersion7(), DocumentId = docId, ChunkIndex = chunkIndex,
        Text = $"art. {chunkIndex}", Source = "ELI", DocType = docType, Title = "Ustawa", Score = 1.0,
    };

    private static readonly Guid ActA = Guid.CreateVersion7();
    private static readonly Guid ActB = Guid.CreateVersion7();
    private static readonly Guid Judgment = Guid.CreateVersion7();

    [Fact] // Rozsiane trafienia w akcie => zakresy wokol kazdego, scalone gdy sie nakladaja.
    public void Spread_hits_produce_merged_ranges()
    {
        var final = new[] { Chunk(ActA, 3), Chunk(ActA, 4), Chunk(ActA, 12) };

        var plan = ArticleNeighbourhood.Plan(final, minChunks: 3, radius: 2);

        // 3 i 4 z promieniem 2 dają 1..6 (nakładają się → jeden zakres); 12 daje 10..14.
        Assert.Equal(2, plan.Count);
        Assert.Contains(plan, r => r.DocumentId == ActA && r.FromIndex == 1 && r.ToIndex == 6);
        Assert.Contains(plan, r => r.DocumentId == ActA && r.FromIndex == 10 && r.ToIndex == 14);
    }

    [Fact] // Ponizej progu koncentracji => dokument sie NIE kwalifikuje. To jest mechanizm, ktory
           // ogranicza zasieg zmiany: zwykle pytania z rozproszonymi zrodlami zachowuja sie jak dzis.
    public void Document_below_concentration_threshold_is_skipped()
    {
        var final = new[] { Chunk(ActA, 3), Chunk(ActA, 7), Chunk(ActB, 1) };

        var plan = ArticleNeighbourhood.Plan(final, minChunks: 3, radius: 2);

        Assert.Empty(plan);
    }

    [Fact] // Dwa akty jednoczesnie przekraczajace prog => dwa niezalezne zestawy zakresow.
    public void Two_qualifying_documents_are_both_expanded()
    {
        var final = new[]
        {
            Chunk(ActA, 5), Chunk(ActA, 6), Chunk(ActA, 7),
            Chunk(ActB, 20), Chunk(ActB, 21), Chunk(ActB, 22),
        };

        var plan = ArticleNeighbourhood.Plan(final, minChunks: 3, radius: 1);

        Assert.Equal(2, plan.Count);
        Assert.Contains(plan, r => r.DocumentId == ActA && r.FromIndex == 4 && r.ToIndex == 8);
        Assert.Contains(plan, r => r.DocumentId == ActB && r.FromIndex == 19 && r.ToIndex == 23);
    }

    [Fact] // ORZECZENIA pomijane niezaleznie od liczby trafien: ich segment to dlugie uzasadnienie,
           // wiec sasiedztwo nie ma tam struktury artykulowej, a pelny tekst wyroku to narracja.
    public void Judgments_are_never_expanded()
    {
        var final = Enumerable.Range(1, 8).Select(i => Chunk(Judgment, i, DocTypes.Judgment)).ToList();

        var plan = ArticleNeighbourhood.Plan(final, minChunks: 3, radius: 2);

        Assert.Empty(plan);
    }

    [Fact] // radius = 0 => wylacznik mechanizmu (idiom istniejacego CitationBridgeArticles).
    public void Zero_radius_disables_mechanism()
    {
        var final = new[] { Chunk(ActA, 3), Chunk(ActA, 4), Chunk(ActA, 5) };

        Assert.Empty(ArticleNeighbourhood.Plan(final, minChunks: 3, radius: 0));
    }

    [Fact] // Trafienie na POCZATKU aktu nie generuje ujemnego ChunkIndex.
    public void Range_is_clamped_at_zero()
    {
        var final = new[] { Chunk(ActA, 0), Chunk(ActA, 1), Chunk(ActA, 2) };

        var plan = ArticleNeighbourhood.Plan(final, minChunks: 3, radius: 5);

        Assert.Equal(0, Assert.Single(plan).FromIndex);
    }

    [Fact] // Pusta lista wejsciowa => pusty plan, bez wyjatku.
    public void Empty_input_produces_empty_plan() =>
        Assert.Empty(ArticleNeighbourhood.Plan([], minChunks: 3, radius: 2));

    [Fact] // Zakresy tego samego dokumentu sa POSORTOWANE rosnaco - akt ma sie czytac liniowo.
    public void Ranges_are_ordered()
    {
        var final = new[] { Chunk(ActA, 30), Chunk(ActA, 5), Chunk(ActA, 17) };

        var plan = ArticleNeighbourhood.Plan(final, minChunks: 3, radius: 1);

        Assert.Equal([4, 16, 29], plan.Select(r => r.FromIndex).ToList());
    }
}
