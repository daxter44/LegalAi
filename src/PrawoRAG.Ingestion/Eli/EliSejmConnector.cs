using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
        var emitted = 0;
        foreach (var addr in _opt.Acts)
        {
            if (request.MaxItems is { } max && emitted >= max) yield break;
            var raw = await FetchActAsync(addr.Trim(), ct);
            if (raw is not null)
            {
                yield return raw;
                emitted++;
            }
        }
    }

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
            DateTimeOffset? changeDate =
                root.TryGetProperty("changeDate", out var cd) && cd.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(cd.GetString(), CultureInfo.InvariantCulture, out var dt) ? dt : null;

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
