using System.Text.Json;
using PrawoRAG.Api.Services;

namespace PrawoRAG.Tests.Chat;

/// <summary>
/// Odczyt źródeł z jsonb przy wczytywaniu historii rozmów: nowe wpisy = pełny ChatSource; stare
/// (sprzed rozszerzenia zapisu) mają tylko Index/Label/Url — muszą wczytać się bez wyjątku,
/// z pustymi brakującymi polami. Round-trip nowego formatu wierny (snippet, chip nowelizacji).
/// </summary>
public class ConversationSourcesTests
{
    [Fact]
    public void New_format_round_trips_full_chat_source()
    {
        var sources = new List<ChatSource>
        {
            new(1, "KPC, art. 367", "Kodeks postępowania cywilnego", "https://isap...", "Treść przepisu…", "2026-07-08"),
        };
        var json = JsonSerializer.SerializeToDocument(sources); // jak AddAssistantMessageAsync

        var restored = ConversationStore.ParseSources(json);

        var s = Assert.Single(restored);
        Assert.Equal(1, s.Index);
        Assert.Equal("KPC, art. 367", s.Label);
        Assert.Equal("Kodeks postępowania cywilnego", s.Title);
        Assert.Equal("Treść przepisu…", s.Snippet);
        Assert.Equal("2026-07-08", s.AmendmentEffectiveDate); // chip nowelizacji przeżywa round-trip
    }

    [Fact]
    public void Legacy_format_with_only_index_label_url_is_tolerated()
    {
        // Dokładny kształt starego zapisu: sources.Select(s => new { s.Index, s.Label, s.Url }).
        var json = JsonDocument.Parse("""[{"Index":2,"Label":"SA Warszawa, I ACa 1/23","Url":"https://saos..."}]""");

        var restored = ConversationStore.ParseSources(json);

        var s = Assert.Single(restored);
        Assert.Equal(2, s.Index);
        Assert.Equal("SA Warszawa, I ACa 1/23", s.Label);
        Assert.Equal("https://saos...", s.Url);
        Assert.Equal("", s.Title);   // brakujące pola → puste, bez wyjątku
        Assert.Equal("", s.Snippet);
        Assert.Null(s.AmendmentEffectiveDate);
    }

    [Fact]
    public void Null_or_malformed_json_yields_empty_list()
    {
        Assert.Empty(ConversationStore.ParseSources(null));
        Assert.Empty(ConversationStore.ParseSources(JsonDocument.Parse("\"nie-tablica\"")));
        Assert.Empty(ConversationStore.ParseSources(JsonDocument.Parse("[42, \"tekst\"]"))); // śmieciowe elementy pomijane
    }
}
