using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Sources;

namespace PrawoRAG.Ingestion.EurLex;

/// <summary>
/// Konektor EUR-Lex/CELLAR (Faza 2 planu prawa UE): pobiera TREŚĆ aktów, które ją niosą, do magazynu
/// surowych. Zakres i klasyfikację dostarcza <see cref="EurLexDiscovery"/> — tu jest tylko decyzja
/// „skąd wziąć tekst" i sumienne pomijanie tego, czego brać nie wolno.
///
/// Kolejność prób i każde pominięcie wynikają z pomiaru (2026-08-26), nie z ostrożności na zapas:
/// <list type="number">
/// <item>strona eur-lex.europa.eu odbija automat (HTTP 202, 0 bajtów) — pobieramy WYŁĄCZNIE z CELLAR-a;</item>
/// <item>najnowsza polska konsolidacja jest pierwsza nie tylko dla aktualności, ale i dla POKRYCIA:
/// tekst bazowy REACH i e-Privacy nie ma polskiej wersji (404), a skonsolidowany ma;</item>
/// <item>404 na jednym formacie NIE znaczy „nie ma po polsku" (akt 32004L0029: XHTML 404, PDF 200);</item>
/// <item>odpowiedź 404 z CELLAR-a ma ciało („does not hold a content datastream…", 214 bajtów) —
/// bez progu <see cref="EurLexOptions.MinContentBytes"/> taki komunikat wjechałby do magazynu jako dokument;</item>
/// <item>akt klasy <see cref="EuActClass.AmendingAbsorbed"/> NIE jest pobierany: jego treść to instrukcja
/// zmiany, która już jest w korpusie w formie normy (tekst skonsolidowany aktu bazowego);</item>
/// <item>akt z flagą <c>MetadataDegraded</c> nie jest pobierany, bo przy niepełnych metadanych nie wiemy,
/// czy nie bierzemy prawa w brzmieniu przed zmianami.</item>
/// </list>
/// Idempotencja i wznawialność = magazyn surowych (jak SAOS/ELI): akt raz pobrany nie jest pobierany ponownie.
/// </summary>
public sealed class EurLexConnector(
    HttpClient http,
    EurLexDiscovery discovery,
    IOptions<EurLexOptions> options,
    ILogger<EurLexConnector> log) : ISourceConnector
{
    private readonly EurLexOptions _opt = options.Value;

    /// <summary>camelCase, bo normalizer czyta pola po nazwach „celex", „textVersion", „actClass", …</summary>
    private static readonly JsonSerializerOptions PayloadJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string Source => SourceKeys.EurLex;

    /// <summary>
    /// <see cref="FetchRequest.SinceModificationDate"/> jest świadomie ignorowane: CELLAR nie daje taniego
    /// strumienia „zmienione od" dla wybranego zakresu. Aktualność zapewnia wybór najnowszej konsolidacji
    /// + <c>content_hash</c> w pipeline (nowa konsolidacja = zmiana hasha = podmiana chunków), a delta
    /// dzienna to osobny tryb (analog <c>sync-eli</c>).
    /// </summary>
    public async IAsyncEnumerable<RawDocument> FetchAsync(FetchRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var acts = await discovery.DiscoverAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        int emitted = 0, metadataOnly = 0, degradedSkipped = 0, noPolishText = 0;

        foreach (var act in acts)
        {
            if (request.MaxItems is { } max && emitted >= max) break;

            if (!act.Class.CarriesOwnText())
            {
                metadataOnly++;
                continue;
            }

            if (act.MetadataDegraded)
            {
                degradedSkipped++;
                log.LogWarning("EUR-Lex {Celex}: niepełne metadane — pomijam pobranie treści (powtórz odkrywanie).", act.Celex);
                continue;
            }

            var raw = await FetchActAsync(act, today, ct);
            if (raw is null)
            {
                noPolishText++;
                continue;
            }

            yield return raw;
            emitted++;
        }

        log.LogInformation(
            "EUR-Lex fetch: pobrano {Emitted}; pominięto — metadane-only {MetaOnly}, niepełne metadane {Degraded}, "
            + "bez tekstu PL {NoText}.", emitted, metadataOnly, degradedSkipped, noPolishText);
    }

    private async Task<RawDocument?> FetchActAsync(EurLexAct act, DateOnly today, CancellationToken ct)
    {
        try
        {
            foreach (var textCelex in act.TextCandidates(today))
            {
                var xhtml = await TryGetTextAsync(textCelex, "application/xhtml+xml", ct);
                if (xhtml is null) continue;

                var consolidated = !textCelex.Equals(act.Celex, StringComparison.OrdinalIgnoreCase);
                log.LogInformation("EUR-Lex {Celex}: XHTML z {TextCelex} ({Version}).",
                    act.Celex, textCelex, consolidated ? "skonsolidowany" : "bazowy");
                return Build(act, textCelex, consolidated ? "consolidated" : "base", xhtml, ContentFormats.Html);
            }

            // Świadomie BEZ ścieżki PDF: akty sprzed 2004 r. mają polski tekst tylko w PDF wydania
            // specjalnego, ale ich ekstrakcja wymaga czytania geometrycznego (zmierzone: „Page.Text"
            // zlepia litery bez spacji, a dwie kolumny przeplatają się zdanie w zdanie). To jest Faza 6
            // z osobną bramką jakości — dorzucenie tu ekstrakcji „jakiejkolwiek" wpuściłoby do korpusu
            // tekst, którego nie da się zacytować. Te akty raportujemy jako niepobrane, nie udajemy pokrycia.
            log.LogWarning("EUR-Lex {Celex}: brak polskiego XHTML-a — pomijam (ścieżka PDF to Faza 6).", act.Celex);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Pomijam akt EUR-Lex {Celex} (błąd pobrania).", act.Celex);
            return null;
        }
    }

    private RawDocument Build(EurLexAct act, string textCelex, string textVersion, string content, string format)
    {
        var consolidationDate = textVersion == "consolidated"
            ? EurLexSparql.ConsolidationDate(textCelex)
            : null;

        var payload = JsonSerializer.SerializeToElement(new EurLexPayload(
            Celex: act.Celex,
            TextCelex: textCelex,
            TextVersion: textVersion,
            ConsolidationDate: consolidationDate?.ToString("yyyy-MM-dd"),
            ActClass: act.Class.ToMetadataValue(),
            Title: act.PolishTitle is null ? null : EuActClassifier.NormalizeWhitespace(act.PolishTitle),
            Amends: act.Relations.Amends,
            Repeals: act.Relations.Repeals,
            Language: _opt.Language), PayloadJson);

        return new RawDocument
        {
            Source = SourceKeys.EurLex,
            ExternalId = act.Celex, // tożsamość aktu = CELEX BAZOWY; wersja tekstu żyje w payloadzie
            DocType = DocTypes.EuAct,
            RawContent = content,
            ContentFormat = format,
            SourceUrl = $"https://eur-lex.europa.eu/legal-content/PL/TXT/?uri=CELEX:{act.Celex}",
            // Data konsolidacji to najlepszy dostępny znacznik stanu prawnego wziętego tekstu.
            SourceModificationDate = consolidationDate is { } d
                ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                : null,
            SourcePayload = payload,
        };
    }

    private async Task<string?> TryGetTextAsync(string celex, string accept, CancellationToken ct)
    {
        using var resp = await SendAsync(celex, accept, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync(ct);
        return IsTooShort(celex, body.Length, accept) ? null : body;
    }

    /// <summary>Odpowiedź krótsza niż próg to komunikat serwera, nie akt prawny — traktujemy jak brak treści.</summary>
    private bool IsTooShort(string celex, int length, string accept)
    {
        if (length >= _opt.MinContentBytes) return false;
        log.LogWarning("EUR-Lex {Celex}: odpowiedź {Accept} ma {Length} B (< {Min}) — to komunikat, nie treść.",
            celex, accept, length, _opt.MinContentBytes);
        return true;
    }

    private async Task<HttpResponseMessage> SendAsync(string celex, string accept, CancellationToken ct)
    {
        if (_opt.RequestDelayMs > 0) await Task.Delay(_opt.RequestDelayMs, ct); // uprzejmość wobec CELLAR-a
        using var req = new HttpRequestMessage(HttpMethod.Get, celex);
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        req.Headers.AcceptLanguage.Clear();
        req.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue(_opt.Language));
        return await http.SendAsync(req, ct);
    }

    /// <summary>Metadane źródłowe aktu UE w <see cref="RawDocument.SourcePayload"/> — czyta je normalizer (Faza 3).</summary>
    private sealed record EurLexPayload(
        string Celex,
        string TextCelex,
        string TextVersion,
        string? ConsolidationDate,
        string ActClass,
        string? Title,
        IReadOnlyList<string> Amends,
        IReadOnlyList<string> Repeals,
        string Language);
}
