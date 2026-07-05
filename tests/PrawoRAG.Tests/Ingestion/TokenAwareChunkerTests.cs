using Microsoft.Extensions.Options;
using PrawoRAG.Ingestion.Chunking;
using PrawoRAG.Ingestion.Saos;
using PrawoRAG.Tests.Fakes;
using PrawoRAG.Tests.Fixtures;

namespace PrawoRAG.Tests.Ingestion;

/// <summary>T-CHUNK — chunking z limitem tokenów (atrapa liczy tokeny = słowa).</summary>
public class TokenAwareChunkerTests
{
    private static TokenAwareChunker Build(ChunkerOptions opt) =>
        new(new FakeEmbeddingProvider(), Options.Create(opt));

    private static readonly ChunkerOptions Small = new() { TargetTokens = 50, MaxTokens = 60, OverlapTokens = 10 };

    [Fact] // T-CHUNK #1: każdy chunk ≤ MaxTokens
    public async Task Respects_max_tokens()
    {
        var doc = new JudgmentNormalizer().Normalize(SaosFixtures.LoadJudgment(227221));
        var chunks = await Build(Small).ChunkAsync(doc, default);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.True(c.TokenCount <= Small.MaxTokens, $"chunk {c.ChunkIndex} ma {c.TokenCount} tokenów"));
    }

    [Fact] // T-CHUNK #2: zakładka między sąsiednimi chunkami (gdy >1 chunk)
    public async Task Adjacent_chunks_overlap()
    {
        var doc = new JudgmentNormalizer().Normalize(SaosFixtures.LoadJudgment(227221));
        var chunks = await Build(Small).ChunkAsync(doc, default);

        if (chunks.Count < 2) return; // brak materiału na overlap
        var hasOverlap = Enumerable.Range(0, chunks.Count - 1)
            .Any(k => chunks[k + 1].CharStart < chunks[k].CharEnd && chunks[k + 1].Section == chunks[k].Section);
        Assert.True(hasOverlap, "oczekiwano zakładki między sąsiednimi chunkami w tej samej sekcji");
    }

    [Fact] // T-CHUNK #3: offsety w granicach i monotoniczny indeks; tekst chunka obecny w oryginale
    public async Task Char_offsets_and_index_are_consistent()
    {
        var doc = new JudgmentNormalizer().Normalize(SaosFixtures.LoadJudgment(227221));
        var chunks = await Build(Small).ChunkAsync(doc, default);

        for (var i = 0; i < chunks.Count; i++)
        {
            Assert.Equal(i, chunks[i].ChunkIndex);
            Assert.InRange(chunks[i].CharStart, 0, doc.PlainText.Length);
            var firstLine = chunks[i].Text.Split('\n')[0];
            Assert.Contains(firstLine, doc.PlainText);
        }
    }

    [Fact] // T-CHUNK #4: krótki-lecz-treściwy tekst → 1 chunk, pusty → 0, bez wyjątku
    public async Task Short_and_empty_inputs()
    {
        var chunker = Build(new ChunkerOptions());
        var empty = await chunker.ChunkAsync(MakeDoc(""), default);
        Assert.Empty(empty);

        // ≥5 sensownych słów → przechodzi filtr MinSubstantiveWords
        var oneLine = await chunker.ChunkAsync(MakeDoc("Sąd oddalił powództwo jako oczywiście bezzasadne."), default);
        Assert.Single(oneLine);
    }

    [Fact] // T-CHUNK #5: zdegenerowane fragmenty (checkboxy, „⚫", pojedyncze znaki) są odrzucane
    public async Task Drops_degenerate_low_content_chunks()
    {
        var chunker = Build(new ChunkerOptions());
        // Same artefakty formularza / HTML→tekst, żadnej realnej treści → 0 chunków.
        var junk = await chunker.ChunkAsync(MakeDoc("⚫\n(\n)\n☐\n☒\n1)\n3.\nUZASADNIENIE"), default);
        Assert.Empty(junk);

        // Próg konfigurowalny: przy MinSubstantiveWords=0 nic nie odrzucamy.
        var permissive = Build(new ChunkerOptions { MinSubstantiveWords = 0 });
        var kept = await permissive.ChunkAsync(MakeDoc("⚫\n(\nUZASADNIENIE"), default);
        Assert.NotEmpty(kept);
    }

    private static PrawoRAG.Domain.Documents.NormalizedDocument MakeDoc(string text) => new()
    {
        Source = "SAOS", ExternalId = "x", DocType = "judgment", Title = "t",
        PlainText = text, ContentHash = "h",
        Segments = string.IsNullOrEmpty(text) ? [] :
        [
            new PrawoRAG.Domain.Documents.DocumentSegment { Text = text, Kind = "section", Label = "document", CharStart = 0 }
        ],
    };
}
