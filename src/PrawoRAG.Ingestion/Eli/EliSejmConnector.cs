using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Sources;

namespace PrawoRAG.Ingestion.Eli;

/// <summary>
/// Konektor ELI/Sejm (api.sejm.gov.pl/eli). Dla każdego skonfigurowanego adresu aktu pobiera
/// metadane (JSON) + tekst ujednolicony (text.html) i buduje <see cref="RawDocument"/> z DocType=act.
/// Idempotencja/wznawialność = magazyn surowych (jak SAOS). Loguje tytuł aktu → zła pozycja ELI
/// jest natychmiast widoczna.
/// </summary>
public sealed class EliSejmConnector(
    HttpClient http, IOptions<EliOptions> options, Pdf.IPdfTextExtractor pdf, ILogger<EliSejmConnector> log) : ISourceConnector
{
    private readonly EliOptions _opt = options.Value;

    public string Source => SourceKeys.Eli;

    public async IAsyncEnumerable<RawDocument> FetchAsync(FetchRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        // Adresy: odkryte z list roczników (gdy Discover.Enabled) ∪ ręczna lista Acts. Dedup.
        IEnumerable<string> addresses = _opt.Acts.Select(a => a.Trim());
        if (_opt.Discover.Enabled)
            addresses = (await DiscoverAddressesAsync(ct)).Concat(addresses);
        var toFetch = addresses.Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var emitted = 0;
        foreach (var addr in toFetch)
        {
            if (request.MaxItems is { } max && emitted >= max) yield break;
            var raw = await FetchActAsync(addr, ct);
            if (raw is not null)
            {
                yield return raw;
                emitted++;
            }
        }
    }

    /// <summary>
    /// Odkrywa adresy aktów z list roczników ELI wg konfiguracji Discover (typ + akceptowany status).
    /// Jedno wywołanie na rocznik zwraca wszystkie akty rocznika — filtrujemy po stronie klienta.
    /// NIE wymaga HTML: nowe akty „born-PDF" (2025+) też wchodzą — connector pobierze ich PDF.
    /// </summary>
    public async Task<IReadOnlyList<string>> DiscoverAddressesAsync(CancellationToken ct)
    {
        var d = _opt.Discover;
        var types = new HashSet<string>(d.Types, StringComparer.OrdinalIgnoreCase);
        var statuses = new HashSet<string>(d.Statuses, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        for (var year = d.YearFrom; year <= d.YearTo; year++)
        {
            ct.ThrowIfCancellationRequested();
            List<EliListItem>? items;
            try
            {
                using var resp = await http.GetAsync($"acts/{d.Publisher}/{year}", ct);
                if (!resp.IsSuccessStatusCode)
                {
                    log.LogWarning("ELI lista {Publisher}/{Year}: HTTP {Code} — pomijam rocznik.", d.Publisher, year, (int)resp.StatusCode);
                    continue;
                }
                items = (await resp.Content.ReadFromJsonAsync<EliListResponse>(ct))?.Items;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "ELI lista {Publisher}/{Year} — błąd, pomijam rocznik.", d.Publisher, year);
                continue;
            }
            if (items is null) continue;

            var wanted = items.Where(i => ShouldInclude(i.Eli, i.Type, i.Status, types, statuses))
                .Select(i => i.Eli!).ToList();
            result.AddRange(wanted);
            log.LogInformation("ELI {Publisher}/{Year}: {Wanted} pasujących z {Total}.", d.Publisher, year, wanted.Count, items.Count);
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Predykat wyboru aktu z listy rocznika (czysty — testowalny bez sieci). <paramref name="statuses"/>
    /// pusta = dowolny status; inaczej status musi być na liście (case-insensitive, przekaż HashSet z komparatorem).
    /// NIE wymaga tekstu HTML — connector rozwiązuje treść do najnowszego t.j. i pobiera PDF, gdy brak HTML
    /// (ELI od 2025 publikuje nowe akty tylko w PDF); akt bez pobieralnej treści connector pomija bezpiecznie.</summary>
    public static bool ShouldInclude(string? eli, string? type, string? status,
        IReadOnlyCollection<string> types, IReadOnlyCollection<string> statuses) =>
        !string.IsNullOrWhiteSpace(eli)
        && type is not null && types.Contains(type)
        && (statuses.Count == 0 || (status is not null && statuses.Contains(status)));

    /// <summary>
    /// Adres najnowszego obwieszczenia z tekstem jednolitym (references „Inf. o tekście jednolitym") lub null.
    /// Wpisy mają tylko „id" (np. „DU/2025/383"), bez daty — najnowszy wyznaczamy deterministycznie z adresu:
    /// max po (rok, pozycja) = chronologicznie najświeższy (pozycje Dz.U. rosną w roku). Czysta — testowalna bez sieci.
    /// </summary>
    public static string? NewestConsolidatedText(JsonElement meta)
    {
        if (meta.ValueKind != JsonValueKind.Object
            || !meta.TryGetProperty("references", out var refs) || refs.ValueKind != JsonValueKind.Object
            || !refs.TryGetProperty("Inf. o tekście jednolitym", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;

        string? best = null;
        var bestKey = (Year: int.MinValue, Pos: int.MinValue);
        foreach (var el in arr.EnumerateArray())
        {
            var id = el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
            if (id is null) continue;
            var parts = id.Split('/');
            if (parts.Length < 3
                || !int.TryParse(parts[^2], out var year) || !int.TryParse(parts[^1], out var pos))
                continue;
            if ((year, pos).CompareTo(bestKey) > 0) { bestKey = (year, pos); best = id; }
        }
        return best;
    }

    /// <summary>
    /// Nowele z „Akty zmieniające" OGŁOSZONE po tekście jednolitym (klucz ELI większy — AKT-1), więc jeszcze
    /// niewchłonięte. Pre-filtr = mała lista nawet dla często nowelizowanych kodeksów. Czysta — testowalna bez
    /// sieci; para z <see cref="NewestConsolidatedText"/> (oba parsują te same <c>references</c>). Współdzielona
    /// przez <c>ActNormalizer</c> (budowa metadanych) i relink (AKT-5.2, odświeżanie listy w stanie ustalonym).
    /// </summary>
    public static List<AmendmentRef> ExtractUnabsorbedAmendments(JsonElement p, string? tjId)
    {
        var list = new List<AmendmentRef>();
        if (tjId is null || p.ValueKind != JsonValueKind.Object
            || !p.TryGetProperty("references", out var refs) || refs.ValueKind != JsonValueKind.Object
            || !refs.TryGetProperty("Akty zmieniające", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var el in arr.EnumerateArray())
        {
            var id = el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
            if (id is null || !Consolidation.IsUnabsorbed(id, tjId)) continue;
            var date = el.TryGetProperty("date", out var dEl) && dEl.ValueKind == JsonValueKind.String ? dEl.GetString() : null;
            list.Add(new AmendmentRef(id, date));
        }
        return list;
    }

    /// <summary>Pobiera SAME metadane aktu (JSON <c>acts/{addr}</c>) — bez text.html/PDF. Tani ruch sieciowy
    /// dla relinku (AKT-5.2): odświeżenie <c>references</c> aktu bazowego bez pobierania jego treści.</summary>
    public Task<JsonDocument> FetchActMetadataAsync(string addr, CancellationToken ct) => FetchMetaAsync(addr, ct);

    private sealed class EliListResponse
    {
        [JsonPropertyName("items")] public List<EliListItem>? Items { get; init; }
    }

    private sealed record EliListItem(
        [property: JsonPropertyName("ELI")] string? Eli,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("textHTML")] bool TextHtml);

    private async Task<RawDocument?> FetchActAsync(string addr, CancellationToken ct)
    {
        // Pojedynczy błąd nie przerywa całości — pomijamy akt (jak SAOS przy pojedynczym orzeczeniu).
        try
        {
            using var metaDoc = await FetchMetaAsync(addr, ct);
            var root = metaDoc.RootElement;

            // AKTUALNOŚĆ: treść bierzemy z NAJNOWSZEGO tekstu jednolitego, nie z (często przestarzałego)
            // text.html aktu bazowego. ELI od 2025 publikuje t.j. tylko w PDF — HTML gdy dostępny, inaczej PDF.
            // Tożsamość dokumentu (ExternalId, tytuł, metadane) zostaje BAZOWA — prawnik cytuje „KK", nie obwieszczenie.
            var tj = NewestConsolidatedText(root);
            var contentAddr = tj ?? addr;
            var contentHtml = tj is null
                ? root.TryGetProperty("textHTML", out var th) && th.ValueKind == JsonValueKind.True
                : await HasHtmlAsync(contentAddr, ct);

            var (rawContent, format) = contentHtml
                ? (await FetchHtmlAsync(contentAddr, ct), ContentFormats.Html)
                : (pdf.ExtractText(await FetchPdfAsync(contentAddr, ct)), ContentFormats.PdfText);

            var title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            var address = root.TryGetProperty("address", out var a) ? a.GetString() : null;
            // AssumeUniversal+AdjustToUniversal: API zwraca datę bez strefy — wymuszamy UTC zamiast
            // dokładania lokalnej strefy maszyny (Npgsql akceptuje dla timestamptz tylko offset 0).
            DateTimeOffset? changeDate =
                root.TryGetProperty("changeDate", out var cd) && cd.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(cd.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt) ? dt : null;

            log.LogInformation("ELI akt {Addr}: {Title} (treść: {Content}, format: {Format})", addr, title, contentAddr, format);

            return new RawDocument
            {
                Source = SourceKeys.Eli,
                ExternalId = addr,
                DocType = DocTypes.Act,
                RawContent = rawContent,
                ContentFormat = format,
                SourceUrl = address is not null
                    ? $"https://isap.sejm.gov.pl/isap.nsf/DocDetails.xsp?id={address}"
                    : $"{_opt.BaseUrl.TrimEnd('/')}/acts/{contentAddr}/text.html",
                SourceModificationDate = changeDate,
                SourcePayload = root.Clone(),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Pomijam akt ELI {Addr} (błąd pobrania).", addr);
            return null;
        }
    }

    private async Task<JsonDocument> FetchMetaAsync(string addr, CancellationToken ct)
    {
        using var resp = await http.GetAsync($"acts/{addr}", ct);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
    }

    private async Task<bool> HasHtmlAsync(string addr, CancellationToken ct)
    {
        using var doc = await FetchMetaAsync(addr, ct);
        return doc.RootElement.TryGetProperty("textHTML", out var th) && th.ValueKind == JsonValueKind.True;
    }

    private async Task<string> FetchHtmlAsync(string addr, CancellationToken ct)
    {
        // Endpoint text.html odrzuca Accept: application/json (406) — żądamy jawnie text/html.
        using var req = new HttpRequestMessage(HttpMethod.Get, $"acts/{addr}/text.html");
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private async Task<byte[]> FetchPdfAsync(string addr, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"acts/{addr}/text.pdf");
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/pdf"));
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }
}
