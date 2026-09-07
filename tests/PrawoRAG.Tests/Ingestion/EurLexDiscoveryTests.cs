using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrawoRAG.Ingestion.EurLex;
using PrawoRAG.Tests.Fixtures;

namespace PrawoRAG.Tests.Ingestion;

/// <summary>
/// T-UE-1c — odkrywanie zakresu prawa UE na atrapie HTTP (bez sieci). Każde pilnowane tu zachowanie
/// jest wnioskiem z realnego przebiegu, nie ozdobą: stronicowanie kończy pusta strona ALBO błąd
/// endpointu (Virtuoso zwraca 500 przy zbyt dużym OFFSET-cie) ALBO powtórzona strona (endpoint
/// ignorujący OFFSET zawieszał pętlę); zapytania idą PORCJAMI (7 756 aktów to inaczej tyleż zapytań
/// na kategorię); a awaria zapytania o metadane musi być JAWNIE oznaczona, bo „nie udało się zapytać"
/// to nie to samo co „nie ma konsolidacji".
/// </summary>
public class EurLexDiscoveryTests
{
    /// <summary>Atrapa endpointu SPARQL: odpowiedź wybierana po fragmencie zapytania w URL-u.</summary>
    private sealed class FakeSparql(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Queries { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var query = Uri.UnescapeDataString(req.RequestUri!.Query);
            Queries.Add(query);
            return Task.FromResult(respond(query));
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/sparql-results+json"),
    };

    private static HttpResponseMessage Error(HttpStatusCode code) => new(code)
    {
        Content = new StringContent("nope"),
    };

    /// <summary>Atrapa serwująca wszystkie trzy odpowiedzi metadanowe (relacje, tytuły, konsolidacje).</summary>
    private static FakeSparql CompleteMetadata() => new(q =>
        q.Contains("amends") ? Json(EurLexFixtures.Read(EurLexFixtures.Relations))
        : q.Contains("expression_title") ? Json(EurLexFixtures.Read(EurLexFixtures.Titles))
        : q.Contains("act_consolidated") ? Json(EurLexFixtures.Read(EurLexFixtures.Consolidations))
        : Json(EurLexFixtures.Read(EurLexFixtures.EmptyPage)));

    private static EurLexDiscovery Discovery(FakeSparql handler, EurLexOptions opt)
    {
        opt.RequestDelayMs = 0; // testy nie czekają na uprzejmość wobec serwera
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://publications.europa.eu/resource/celex/") };
        return new EurLexDiscovery(http, new OptionsWrapper<EurLexOptions>(opt), NullLogger<EurLexDiscovery>.Instance);
    }

    [Fact] // Strona krótsza od PageSize = koniec listy (jedno zapytanie, bez pytania o kolejny OFFSET).
    public async Task Paging_stops_on_short_page()
    {
        var handler = new FakeSparql(_ => Json(EurLexFixtures.Read(EurLexFixtures.DiscoverPage)));
        var sut = Discovery(handler, new EurLexOptions { Discover = { Enabled = true, PageSize = 3000 } });

        var celexes = await sut.DiscoverCelexAsync(default);

        Assert.Equal(25, celexes.Count);
        Assert.Single(handler.Queries);
    }

    [Fact] // BEZPIECZNIK: endpoint ignorujący OFFSET oddaje w kółko tę samą stronę. Bez wykrycia
           // „strona nie wniosła nic nowego" pętla kręci się w nieskończoność i wygląda jak zawieszenie
           // (złapane realnie: pierwszy przebieg tego testu nie skończył się w 10 minut).
    public async Task Paging_stops_when_page_repeats()
    {
        var handler = new FakeSparql(_ => Json(EurLexFixtures.Read(EurLexFixtures.DiscoverPage)));
        var sut = Discovery(handler, new EurLexOptions { Discover = { Enabled = true, PageSize = 25 } });

        var celexes = await sut.DiscoverCelexAsync(default);

        Assert.Equal(25, celexes.Count);
        Assert.Equal(2, handler.Queries.Count); // pierwsza strona + jedna próba z OFFSET, potem stop
        Assert.Contains(handler.Queries, q => q.Contains("OFFSET 25"));
    }

    [Fact] // Pusta strona = koniec listy (bez wyjątku).
    public async Task Paging_stops_on_empty_page()
    {
        var calls = 0;
        var handler = new FakeSparql(_ => Json(calls++ == 0
            ? EurLexFixtures.Read(EurLexFixtures.DiscoverPage)
            : EurLexFixtures.Read(EurLexFixtures.EmptyPage)));
        var sut = Discovery(handler, new EurLexOptions { Discover = { Enabled = true, PageSize = 25 } });

        Assert.Equal(25, (await sut.DiscoverCelexAsync(default)).Count);
    }

    [Fact] // ZMIERZONE: przy zbyt dużym OFFSET-cie endpoint zwraca 500. To koniec listy, nie awaria przebiegu.
    public async Task Server_error_ends_paging_without_throwing()
    {
        var calls = 0;
        var handler = new FakeSparql(_ => calls++ == 0
            ? Json(EurLexFixtures.Read(EurLexFixtures.DiscoverPage))
            : Error(HttpStatusCode.InternalServerError));
        var sut = Discovery(handler, new EurLexOptions { Discover = { Enabled = true, PageSize = 25 } });

        Assert.Equal(25, (await sut.DiscoverCelexAsync(default)).Count);
    }

    [Fact] // Priorytetowe akty z konfiguracji idą PRZED odkrytymi (pierwsza transza = zestaw pomiarowy).
    public async Task Priority_acts_come_first_and_are_classified()
    {
        var handler = CompleteMetadata();
        var opt = new EurLexOptions { Acts = ["32016R0679", "32018R0070", "32005R0080"] };

        var acts = await Discovery(handler, opt).DiscoverAsync(default);

        Assert.Equal(["32016R0679", "32018R0070", "32005R0080"], acts.Select(a => a.Celex));
        Assert.Equal(EuActClass.Substantive, acts[0].Class);   // RODO — własna treść (mimo relacji „uchyla")
        Assert.True(acts[0].Class.CarriesOwnText());
        Assert.Equal(EuActClass.AmendingOpen, acts[1].Class);  // nowela bez konsolidacji wchłaniającej
        Assert.Contains("02016R0679-20160504", acts[0].TextCandidates(new DateOnly(2026, 8, 26)));
        Assert.NotNull(acts[0].PolishTitle);
    }

    [Fact] // Nowela, której zmiany są już w konsolidacji aktu bazowego, nie wnosi treści.
    public async Task Absorbed_amendment_is_metadata_only()
    {
        // Odpowiedź o konsolidacje niesie tu konsolidację OBCEGO aktu (02005R0396-…), czyli dowód wchłonięcia.
        const string consolidations = """
            { "head": { "vars": ["base", "cons"] }, "results": { "bindings": [
              { "base": { "type": "literal", "value": "32018R0070" },
                "cons": { "type": "literal", "value": "02005R0396-20180216" } } ] } }
            """;
        var handler = new FakeSparql(q =>
            q.Contains("amends") ? Json(EurLexFixtures.Read(EurLexFixtures.Relations))
            : q.Contains("expression_title") ? Json(EurLexFixtures.Read(EurLexFixtures.Titles))
            : q.Contains("act_consolidated") ? Json(consolidations)
            : Json(EurLexFixtures.Read(EurLexFixtures.EmptyPage)));

        var act = Assert.Single(await Discovery(handler, new EurLexOptions { Acts = ["32018R0070"] }).DiscoverAsync(default));

        Assert.Equal(EuActClass.AmendingAbsorbed, act.Class);
        Assert.False(act.Class.CarriesOwnText());
        Assert.Empty(act.Consolidations); // konsolidacja OBCEGO aktu nie jest kandydatem na treść tego aktu
    }

    [Fact] // Zapytania idą porcjami — inaczej 7 756 aktów to 7 756 zapytań na kategorię.
    public async Task Queries_are_batched()
    {
        var handler = new FakeSparql(_ => Json(EurLexFixtures.Read(EurLexFixtures.EmptyPage)));
        var opt = new EurLexOptions { Acts = ["A1", "A2", "A3", "A4", "A5"], BatchSize = 2 };

        await Discovery(handler, opt).DiscoverAsync(default);

        // 3 porcje × 3 zapytania (relacje + tytuły + konsolidacje) = 9; odkrywanie zakresu wyłączone.
        Assert.Equal(9, handler.Queries.Count);
        Assert.Contains(handler.Queries, q => q.Contains("\"A1\"") && q.Contains("\"A2\"") && !q.Contains("\"A3\""));
    }

    [Fact] // Awaria zapytania o metadane nie przerywa odkrywania, ale MUSI być jawnie oznaczona:
           // „nie udało się zapytać" to nie „nie ma konsolidacji". Zmierzone realnie — endpoint CELLAR-a
           // zwraca 502 pod obciążeniem, a cicha degradacja wpuściłaby do korpusu stare brzmienie prawa.
    public async Task Metadata_failure_is_flagged_not_silently_assumed()
    {
        var handler = new FakeSparql(q => q.Contains("amends")
            ? Error(HttpStatusCode.ServiceUnavailable)
            : Json(EurLexFixtures.Read(EurLexFixtures.EmptyPage)));

        var act = Assert.Single(await Discovery(handler, new EurLexOptions { Acts = ["32018R0070"] }).DiscoverAsync(default));

        Assert.True(act.MetadataDegraded);
        Assert.Equal(EuActClass.Substantive, act.Class); // przy niepewnych danych NIE odbieramy treści
    }

    [Fact] // Kompletne metadane = brak flagi (inaczej ostrzeżenie byłoby szumem i przestałoby znaczyć cokolwiek).
    public async Task Complete_metadata_is_not_flagged()
    {
        var act = Assert.Single(await Discovery(CompleteMetadata(), new EurLexOptions { Acts = ["32016R0679"] })
            .DiscoverAsync(default));

        Assert.False(act.MetadataDegraded);
    }
}
