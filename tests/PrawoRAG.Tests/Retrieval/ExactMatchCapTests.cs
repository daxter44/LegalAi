using PrawoRAG.Domain.Retrieval;

namespace PrawoRAG.Tests.Retrieval;

/// <summary>
/// Cap dominacji jednego dokumentu w torach dokładnych (czysta funkcja). Objaw użytkownika: jedno
/// trafienie (sygnatura wyroku) dociągało kilkanaście chunków ze Score=MaxValue i zajmowało cały
/// TopK — „8 źródeł, same wyroki, zero ustawy". Cap rezerwuje sloty na kontekst.
/// </summary>
public class ExactMatchCapTests
{
    private static RetrievedChunk Chunk(Guid doc, string text = "x") => new()
    {
        ChunkId = Guid.NewGuid(), DocumentId = doc, Text = text,
        Source = "SAOS", DocType = "judgment", Title = "Wyrok",
    };

    [Fact] // TopK=8, ReservedSlots=2 → jeden dokument dostaje maks. 6 chunków
    public void MaxPerDocument_reserves_slots()
    {
        Assert.Equal(6, ExactMatchCap.MaxPerDocument(8));
        Assert.Equal(2, ExactMatchCap.ReservedSlots);
    }

    [Fact] // limit nigdy nie spada poniżej 1 (małe TopK)
    public void MaxPerDocument_never_below_one()
        => Assert.Equal(1, ExactMatchCap.MaxPerDocument(1));

    [Fact] // rdzeń: 12 chunków JEDNEGO wyroku → przycięte do maxPerDoc, reszta odpada
    public void Single_document_flood_is_capped()
    {
        var doc = Guid.NewGuid();
        var flood = Enumerable.Range(0, 12).Select(_ => Chunk(doc)).ToList();

        var capped = ExactMatchCap.LimitPerDocument(flood, maxPerDoc: 6);

        Assert.Equal(6, capped.Count);
        Assert.All(capped, c => Assert.Equal(doc, c.DocumentId));
    }

    [Fact] // różne dokumenty każdy ma własny licznik — cap jest PER dokument, nie globalny
    public void Cap_is_per_document_not_global()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var mixed = new[] { Chunk(a), Chunk(a), Chunk(a), Chunk(b), Chunk(b) };

        var capped = ExactMatchCap.LimitPerDocument(mixed, maxPerDoc: 2);

        Assert.Equal(2, capped.Count(c => c.DocumentId == a)); // A przycięte z 3 do 2
        Assert.Equal(2, capped.Count(c => c.DocumentId == b)); // B mieści się w limicie
        Assert.Equal(4, capped.Count);
    }

    [Fact] // kolejność wejścia zachowana; odcinane są PÓŹNIEJSZE (niżej scorowane) chunki dokumentu
    public void Preserves_order_and_drops_later_chunks()
    {
        var doc = Guid.NewGuid();
        var first = Chunk(doc, "pierwszy");
        var second = Chunk(doc, "drugi");
        var third = Chunk(doc, "trzeci");

        var capped = ExactMatchCap.LimitPerDocument(new[] { first, second, third }, maxPerDoc: 2);

        Assert.Equal(new[] { "pierwszy", "drugi" }, capped.Select(c => c.Text));
    }

    [Fact]
    public void Below_cap_passes_through_unchanged()
    {
        var chunks = new[] { Chunk(Guid.NewGuid()), Chunk(Guid.NewGuid()) };
        Assert.Equal(2, ExactMatchCap.LimitPerDocument(chunks, maxPerDoc: 6).Count);
    }
}
