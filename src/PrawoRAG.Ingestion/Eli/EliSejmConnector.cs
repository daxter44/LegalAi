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
public sealed class EliSejmConnector(HttpClient http, IOptions<EliOptions> options, ILogger<EliSejmConnector> log) : ISourceConnector
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
    /// Odkrywa adresy aktów z list roczników ELI wg konfiguracji Discover (typ + status „obowiązujący" +
    /// tekst HTML). Jedno wywołanie na rocznik zwraca wszystkie akty rocznika — filtrujemy po stronie klienta.
    /// </summary>
    public async Task<IReadOnlyList<string>> DiscoverAddressesAsync(CancellationToken ct)
    {
        var d = _opt.Discover;
        var types = new HashSet<string>(d.Types, StringComparer.OrdinalIgnoreCase);
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

            var wanted = items.Where(i => ShouldInclude(i.Eli, i.Type, i.Status, i.TextHtml, types, d.OnlyInForce))
                .Select(i => i.Eli!).ToList();
            result.AddRange(wanted);
            log.LogInformation("ELI {Publisher}/{Year}: {Wanted} pasujących z {Total}.", d.Publisher, year, wanted.Count, items.Count);
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Predykat wyboru aktu z listy rocznika (czysty — testowalny bez sieci).</summary>
    public static bool ShouldInclude(string? eli, string? type, string? status, bool textHtml,
        IReadOnlyCollection<string> types, bool onlyInForce) =>
        !string.IsNullOrWhiteSpace(eli)
        && textHtml
        && type is not null && types.Contains(type)
        && (!onlyInForce || string.Equals(status, "obowiązujący", StringComparison.OrdinalIgnoreCase));

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
            using var metaResp = await http.GetAsync($"acts/{addr}", ct);
            metaResp.EnsureSuccessStatusCode();
            using var metaDoc = JsonDocument.Parse(await metaResp.Content.ReadAsStringAsync(ct));
            var root = metaDoc.RootElement;

            // Endpoint text.html odrzuca Accept: application/json (406) — żądamy jawnie text/html.
            using var htmlReq = new HttpRequestMessage(HttpMethod.Get, $"acts/{addr}/text.html");
            htmlReq.Headers.Accept.Clear();
            htmlReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            using var htmlResp = await http.SendAsync(htmlReq, ct);
            htmlResp.EnsureSuccessStatusCode();
            var html = await htmlResp.Content.ReadAsStringAsync(ct);

            var title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            var address = root.TryGetProperty("address", out var a) ? a.GetString() : null;
            // AssumeUniversal+AdjustToUniversal: API zwraca datę bez strefy — wymuszamy UTC zamiast
            // dokładania lokalnej strefy maszyny (Npgsql akceptuje dla timestamptz tylko offset 0).
            DateTimeOffset? changeDate =
                root.TryGetProperty("changeDate", out var cd) && cd.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(cd.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt) ? dt : null;

            log.LogInformation("ELI akt {Addr}: {Title}", addr, title);

            return new RawDocument
            {
                Source = SourceKeys.Eli,
                ExternalId = addr,
                DocType = DocTypes.Act,
                RawContent = html,
                SourceUrl = address is not null
                    ? $"https://isap.sejm.gov.pl/isap.nsf/DocDetails.xsp?id={address}"
                    : $"{_opt.BaseUrl.TrimEnd('/')}/acts/{addr}/text.html",
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
}
