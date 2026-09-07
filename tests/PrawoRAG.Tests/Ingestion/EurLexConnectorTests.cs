using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Sources;
using PrawoRAG.Ingestion.EurLex;
using PrawoRAG.Tests.Fixtures;

namespace PrawoRAG.Tests.Ingestion;

/// <summary>
/// T-UE-2 — pobieranie treści aktów UE, na atrapie HTTP (bez sieci). Każdy przypadek to sytuacja
/// ZMIERZONA na CELLAR-ze 2026-08-26 i każda z nich, przeoczona, kończy się złym korpusem:
/// tekst bazowy zamiast skonsolidowanego (prawo przed zmianami), komunikat błędu zapisany jako akt,
/// diff wchłonięty w konsolidację zapisany jako norma, albo pobranie treści na podstawie metadanych,
/// których nie udało się potwierdzić.
/// </summary>
public class EurLexConnectorTests
{
    /// <summary>Atrapa CELLAR-a: SPARQL z fixture'ów, treść z mapy (CELEX, Accept) → odpowiedź.</summary>
    private sealed class FakeCellar(
        Dictionary<(string Celex, string Accept), HttpResponseMessage> content,
        string? consolidationsJson = null)
        : HttpMessageHandler
    {
        public List<string> ContentRequests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/sparql", StringComparison.Ordinal))
            {
                var query = Uri.UnescapeDataString(req.RequestUri.Query);
                var body =
                    query.Contains("amends") ? EurLexFixtures.Read(EurLexFixtures.Relations)
                    : query.Contains("expression_title") ? EurLexFixtures.Read(EurLexFixtures.Titles)
                    : query.Contains("act_consolidated") ? consolidationsJson ?? EurLexFixtures.Read(EurLexFixtures.Consolidations)
                    : EurLexFixtures.Read(EurLexFixtures.EmptyPage);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/sparql-results+json"),
                });
            }

            var celex = url[(url.LastIndexOf('/') + 1)..];
            var accept = req.Headers.Accept.First().MediaType!;
            ContentRequests.Add($"{celex}|{accept}");

            return Task.FromResult(content.TryGetValue((celex, accept), out var resp)
                ? Clone(resp)
                // Realny kształt odmowy CELLAR-a: 404 Z CIAŁEM, nie pusta odpowiedź.
                : new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(
                        "cellar identifier cellar:55091694-037d-4044-a818-d1b2c52891e2 does not hold a content "
                        + "datastream of the requested type"),
                });
        }

        private static HttpResponseMessage Clone(HttpResponseMessage source) => new(source.StatusCode)
        {
            Content = new StringContent(source.Content.ReadAsStringAsync().Result, Encoding.UTF8, "application/xhtml+xml"),
        };
    }

    /// <summary>Treść aktu na tyle długa, by przeszła próg <c>MinContentBytes</c>.</summary>
    private static HttpResponseMessage Act(string celex) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $"<html><body><div class=\"eli-subdivision\" id=\"art_1\"><p class=\"oj-ti-art\">Artykuł 1</p>"
            + $"<p class=\"oj-normal\">Treść aktu {celex}. {new string('x', 2500)}</p></div></body></html>",
            Encoding.UTF8, "application/xhtml+xml"),
    };

    private static async Task<(List<RawDocument> Docs, FakeCellar Handler)> Fetch(
        Dictionary<(string, string), HttpResponseMessage> content, List<string> acts,
        Action<EurLexOptions>? tweak = null, int? maxItems = null, string? consolidationsJson = null)
    {
        var opt = new EurLexOptions { Acts = acts, RequestDelayMs = 0 };
        tweak?.Invoke(opt);
        var handler = new FakeCellar(content, consolidationsJson);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://publications.europa.eu/resource/celex/") };
        var wrapped = new OptionsWrapper<EurLexOptions>(opt);
        var discovery = new EurLexDiscovery(http, wrapped, NullLogger<EurLexDiscovery>.Instance);
        var sut = new EurLexConnector(http, discovery, wrapped, NullLogger<EurLexConnector>.Instance);

        var docs = new List<RawDocument>();
        await foreach (var d in sut.FetchAsync(new FetchRequest { MaxItems = maxItems }, default)) docs.Add(d);
        return (docs, handler);
    }

    private static string? Payload(RawDocument doc, string property) =>
        doc.SourcePayload!.Value.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    [Fact] // Treścią dokumentu jest najnowsza polska konsolidacja; tożsamość zostaje BAZOWA (CELEX bazowy).
    public async Task Prefers_newest_polish_consolidation()
    {
        var (docs, handler) = await Fetch(
            new() { [("02016R0679-20160504", "application/xhtml+xml")] = Act("RODO") }, ["32016R0679"]);

        var doc = Assert.Single(docs);
        Assert.Equal("32016R0679", doc.ExternalId);
        Assert.Equal(SourceKeys.EurLex, doc.Source);
        Assert.Equal(DocTypes.EuAct, doc.DocType);
        Assert.Equal(ContentFormats.Html, doc.ContentFormat);
        Assert.Equal("consolidated", Payload(doc, "textVersion"));
        Assert.Equal("02016R0679-20160504", Payload(doc, "textCelex"));
        Assert.Equal("2016-05-04", Payload(doc, "consolidationDate"));
        Assert.Equal("substantive", Payload(doc, "actClass"));
        Assert.Contains("2016/679", Payload(doc, "title"));
        Assert.Equal("02016R0679-20160504|application/xhtml+xml", Assert.Single(handler.ContentRequests));
    }

    [Fact] // Konsolidacja bez wersji PL (realny 404) → zejście na tekst bazowy, nie porzucenie aktu.
    public async Task Falls_back_to_base_text_when_consolidation_missing()
    {
        var (docs, handler) = await Fetch(
            new() { [("32016R0679", "application/xhtml+xml")] = Act("RODO bazowe") }, ["32016R0679"]);

        Assert.Equal("base", Payload(Assert.Single(docs), "textVersion"));
        Assert.Equal(
            ["02016R0679-20160504|application/xhtml+xml", "32016R0679|application/xhtml+xml"],
            handler.ContentRequests);
    }

    [Fact] // 404 z CIAŁEM „does not hold a content datastream…" (214 B) to komunikat, nie akt.
    public async Task Rejects_error_body_masquerading_as_content()
    {
        var (docs, _) = await Fetch(new(), ["32016R0679"]);

        Assert.Empty(docs);
    }

    [Fact] // Odpowiedź 200, ale zbyt krótka, też nie jest aktem (próg MinContentBytes).
    public async Task Rejects_too_short_content()
    {
        var shortBody = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>pusto</body></html>", Encoding.UTF8, "application/xhtml+xml"),
        };

        var (docs, _) = await Fetch(
            new() { [("02016R0679-20160504", "application/xhtml+xml")] = shortBody }, ["32016R0679"]);

        Assert.Empty(docs);
    }

    [Fact] // Nowela wchłonięta w konsolidację NIE jest pobierana — jej treść jest już w korpusie jako norma.
    public async Task Does_not_fetch_absorbed_amendment()
    {
        // Konsolidacja OBCEGO aktu (02005R0396-…) to dowód, że zmiany tej noweli są już wchłonięte.
        const string absorbed = """
            { "head": { "vars": ["base", "cons"] }, "results": { "bindings": [
              { "base": { "type": "literal", "value": "32018R0070" },
                "cons": { "type": "literal", "value": "02005R0396-20180216" } } ] } }
            """;

        var (docs, handler) = await Fetch(
            new() { [("32018R0070", "application/xhtml+xml")] = Act("nowela") }, ["32018R0070"],
            consolidationsJson: absorbed);

        Assert.Empty(docs);
        Assert.Empty(handler.ContentRequests); // ani jednego żądania o treść
    }

    [Fact] // Ścieżka PDF to Faza 6 — brak XHTML-a kończy się pominięciem, nie atrapą ekstrakcji.
    public async Task Skips_act_without_xhtml_instead_of_guessing_pdf()
    {
        var (docs, handler) = await Fetch(new(), ["31973R1545"]);

        Assert.Empty(docs);
        Assert.DoesNotContain(handler.ContentRequests, r => r.Contains("application/pdf"));
    }

    [Fact] // MaxItems ucina przebieg (spike'i i testy dymne na kilku aktach).
    public async Task Honours_max_items()
    {
        var (docs, _) = await Fetch(
            new()
            {
                [("32016R0679", "application/xhtml+xml")] = Act("a"),
                [("02016R0679-20160504", "application/xhtml+xml")] = Act("b"),
                [("32005R0080", "application/xhtml+xml")] = Act("c"),
            },
            ["32016R0679", "32005R0080"], maxItems: 1);

        Assert.Single(docs);
    }

    [Fact] // Relacje aktu jadą do payloadu — z nich żyje aktualność (co uchylone, co zmienione).
    public async Task Carries_relations_in_payload()
    {
        var (docs, _) = await Fetch(
            new() { [("02016R0679-20160504", "application/xhtml+xml")] = Act("RODO") }, ["32016R0679"]);

        var repeals = Assert.Single(docs).SourcePayload!.Value.GetProperty("repeals");
        Assert.Contains("31995L0046", repeals.EnumerateArray().Select(x => x.GetString()));
    }
}
