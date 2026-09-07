using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PrawoRAG.Ingestion.EurLex;

/// <summary>Akt UE odkryty w CELLAR-ze: CELEX, klasa (czy niesie własną treść) i relacje.</summary>
public sealed record EurLexAct(
    string Celex,
    string? PolishTitle,
    EuActClass Class,
    EuActRelations Relations,
    IReadOnlyList<string> Consolidations,
    bool MetadataDegraded)
{
    /// <summary>Kandydaci na treść w kolejności prób (najnowsza własna konsolidacja → … → tekst bazowy).</summary>
    public List<string> TextCandidates(DateOnly today) =>
        EurLexSparql.SelectTextCandidates(Celex, Consolidations, today);
}

/// <summary>
/// Odkrywanie zakresu prawa UE w CELLAR-ze (Faza 1 planu): lista CELEX-ów wg filtra + relacje
/// + wersje skonsolidowane + klasyfikacja. NIE pobiera treści — treść to Faza 2. Dzięki temu wolumen
/// i skład korpusu można poznać (i zaraportować) przed jakimkolwiek masowym pobieraniem.
/// Zapytania do endpointu SPARQL są zbiorcze (porcje po <see cref="EurLexOptions.BatchSize"/>):
/// pytanie per akt to ~7 756 zapytań, porcjami schodzi do ~150 na kategorię.
/// </summary>
public sealed class EurLexDiscovery(
    HttpClient http, IOptions<EurLexOptions> options, ILogger<EurLexDiscovery> log)
{
    private readonly EurLexOptions _opt = options.Value;

    /// <summary>
    /// Pełne odkrycie: CELEX-y (priorytetowe z konfiguracji + odkryte), relacje, konsolidacje, klasa.
    /// Kolejność wyniku = kolejność ingestii: najpierw <see cref="EurLexOptions.Acts"/>, potem odkryte
    /// malejąco po CELEX-ie (czyli od najnowszych roczników).
    /// </summary>
    public async Task<IReadOnlyList<EurLexAct>> DiscoverAsync(CancellationToken ct)
    {
        var celexes = await CollectCelexAsync(ct);
        if (celexes.Count == 0) return [];

        var relations = new Dictionary<string, EuActRelations>(StringComparer.OrdinalIgnoreCase);
        var consolidations = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var absorbedBy = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Akty z porcji, dla której któreś zapytanie o metadane padło. „Nie udało się zapytać" MUSI być
        // odróżnialne od „nie ma konsolidacji" — zmierzone realnie: endpoint CELLAR-a zwraca 502 pod
        // obciążeniem, a cicha degradacja oznaczałaby tekst bazowy zamiast skonsolidowanego (stare prawo
        // w korpusie) albo diff zaklasyfikowany jako akt merytoryczny.
        var degraded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var batch in Chunk(celexes, Math.Max(1, _opt.BatchSize)))
        {
            ct.ThrowIfCancellationRequested();

            if (await QueryAsync(EurLexSparql.BuildRelationsQuery(batch), ct) is { } relJson)
                foreach (var (k, v) in EurLexSparql.ParseRelations(relJson)) relations[k] = v;
            else
            {
                foreach (var c in batch) degraded.Add(c);
                log.LogWarning("EUR-Lex: relacje dla porcji {Count} aktów niedostępne — metadane NIEPEŁNE.", batch.Count);
            }

            // Tytuł polski jest tu podwójnie potrzebny: rozstrzyga klasę aktu (imiesłów „zmieniające"
            // przed własnym „w sprawie") i jest metadaną dokumentu. Brak tytułu PL to zresztą dobre
            // przybliżenie braku polskiego tekstu (6 760 z 7 756 aktów ma tytuł polski).
            if (await QueryAsync(EurLexSparql.BuildTitleQuery(batch), ct) is { } titleJson)
                foreach (var (k, v) in EurLexSparql.ParsePairs(titleJson, "base", "title"))
                    if (v.Count > 0) titles[k] = v[0];
            else
            {
                foreach (var c in batch) degraded.Add(c);
                log.LogWarning("EUR-Lex: tytuły dla porcji {Count} aktów niedostępne — metadane NIEPEŁNE.", batch.Count);
            }

            if (await QueryAsync(EurLexSparql.BuildConsolidationQuery(batch), ct) is { } consJson)
            {
                var pairs = EurLexSparql.ParsePairs(consJson, "base", "cons");
                foreach (var (baseCelex, list) in pairs)
                {
                    // Ta sama odpowiedź niesie DWIE różne informacje, zależnie od prefiksu CELEX-u:
                    // konsolidacje TEGO aktu (prefiks własny) oraz konsolidacje OBCYCH aktów, które ten
                    // akt nowelizuje — czyli dowód, że jego zmiany są już wchłonięte.
                    var prefix = EurLexSparql.ConsolidatedPrefix(baseCelex);
                    var own = prefix is null ? [] : list.Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                    var foreignConsolidations = list.Except(own, StringComparer.OrdinalIgnoreCase).ToList();

                    if (own.Count > 0) consolidations[baseCelex] = own;
                    if (foreignConsolidations.Count > 0) absorbedBy[baseCelex] = foreignConsolidations;
                }
            }
            else
            {
                foreach (var c in batch) degraded.Add(c);
                log.LogWarning("EUR-Lex: konsolidacje dla porcji {Count} aktów niedostępne — metadane NIEPEŁNE "
                    + "(bez nich wybralibyśmy tekst bazowy, czyli prawo w brzmieniu przed zmianami).", batch.Count);
            }
        }

        var acts = celexes.Select(c => new EurLexAct(
            c,
            titles.GetValueOrDefault(c),
            EuActClassifier.Classify(c, titles.GetValueOrDefault(c), relations.GetValueOrDefault(c), absorbedBy.GetValueOrDefault(c)),
            relations.GetValueOrDefault(c) ?? EuActRelations.None,
            consolidations.GetValueOrDefault(c) ?? [],
            degraded.Contains(c))).ToList();

        log.LogInformation(
            "EUR-Lex odkrywanie: {Total} aktów; treść niosą {WithText}, metadane-only {MetaOnly}, bez tytułu PL {NoTitle}.",
            acts.Count, acts.Count(a => a.Class.CarriesOwnText()), acts.Count(a => !a.Class.CarriesOwnText()),
            acts.Count(a => a.PolishTitle is null));

        if (acts.Any(a => a.MetadataDegraded))
            log.LogWarning(
                "EUR-Lex: {Degraded} aktów ma NIEPEŁNE metadane (zapytanie SPARQL padło). Dla nich klasa i wybór wersji "
                + "są niepewne — nie wolno na tej podstawie pobierać treści; powtórz odkrywanie dla tych CELEX-ów.",
                acts.Count(a => a.MetadataDegraded));
        return acts;
    }

    /// <summary>CELEX-y: priorytetowe z konfiguracji + (gdy włączone) odkryte zapytaniem zakresowym.</summary>
    private async Task<List<string>> CollectCelexAsync(CancellationToken ct)
    {
        IEnumerable<string> all = _opt.Acts.Select(a => a.Trim()).Where(a => a.Length > 0);
        if (_opt.Discover.Enabled) all = all.Concat(await DiscoverCelexAsync(ct));
        return all.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Same CELEX-y wg filtra zakresu, ze stronicowaniem. Koniec listy = PUSTA strona albo błąd
    /// endpointu (zmierzone: przy zbyt dużym OFFSET-cie Virtuoso zwraca 500) — oba przerywają
    /// pętlę bez wywalania przebiegu. Publiczna: tryb „discover" pokazuje wolumen bez treści.
    /// </summary>
    public async Task<IReadOnlyList<string>> DiscoverCelexAsync(CancellationToken ct)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var json = await QueryAsync(EurLexSparql.BuildDiscoverQuery(_opt.Discover, offset), ct);
            if (json is null)
            {
                log.LogWarning("EUR-Lex odkrywanie: SPARQL odmówił przy OFFSET {Offset} — traktuję jako koniec listy ({Total} aktów).", offset, result.Count);
                break;
            }

            var page = EurLexSparql.ParseColumn(json, "celex");
            if (page.Count == 0) break;

            // Bezpiecznik na endpoint, który ignoruje OFFSET (albo zwraca w kółko tę samą stronę):
            // bez niego pętla kręci się w nieskończoność, a wygląda jak „długo trwa odkrywanie".
            var added = page.Count(c => seen.Add(c));
            result.AddRange(page);
            log.LogInformation("EUR-Lex odkrywanie: +{Page} (nowych {Added}, razem {Total}).", page.Count, added, seen.Count);
            if (added == 0)
            {
                log.LogWarning("EUR-Lex odkrywanie: strona z OFFSET {Offset} nie wniosła nowych aktów — kończę stronicowanie.", offset);
                break;
            }

            if (page.Count < _opt.Discover.PageSize) break;
            offset += _opt.Discover.PageSize;
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Zapytanie do endpointu SPARQL; null przy odpowiedzi błędnej (wołający decyduje, co to znaczy).</summary>
    private async Task<string?> QueryAsync(string query, CancellationToken ct)
    {
        if (_opt.RequestDelayMs > 0) await Task.Delay(_opt.RequestDelayMs, ct); // uprzejmość wobec CELLAR-a
        var url = $"{_opt.SparqlUrl}?query={Uri.EscapeDataString(query)}"
            + $"&format={Uri.EscapeDataString("application/sparql-results+json")}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Clear();
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/sparql-results+json"));
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("SPARQL HTTP {Code}.", (int)resp.StatusCode);
                return null;
            }
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Zapytanie SPARQL nie powiodło się.");
            return null;
        }
    }

    private static IEnumerable<List<string>> Chunk(List<string> items, int size)
    {
        for (var i = 0; i < items.Count; i += size)
            yield return items.GetRange(i, Math.Min(size, items.Count - i));
    }
}
