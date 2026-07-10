using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Sources;
using PrawoRAG.Ingestion.Storage;

namespace PrawoRAG.Ingestion.Saos;

/// <summary>
/// Konektor SAOS. Dwie ścieżki (API SAOS to wymusza — zob. plan 1.1):
/// • initial (request.SinceModificationDate == null): search API z filtrami wycinka → ID → pełny dokument /judgments/{id};
/// • incremental: dump API z sinceModificationDate + filtr wycinka po stronie klienta.
/// </summary>
public sealed class SaosConnector(
    HttpClient http, IOptions<SaosOptions> options, IOptions<RawStoreOptions> rawStoreOptions,
    ILogger<SaosConnector> log, IRawDocumentStore store) : ISourceConnector
{
    private readonly SaosOptions _opt = options.Value;
    private readonly string _rawStoreRoot = rawStoreOptions.Value.RootPath;

    /// <summary>Ile stron cofnąć się od ostatniego zapisanego checkpointu przy wznowieniu — margines
    /// bezpieczeństwa (re-skanowane strony są tanie: skip-check bez pobrania pełnej treści).</summary>
    private const int CheckpointOverlapPages = 20;

    public string Source => SourceKeys.Saos;

    public async IAsyncEnumerable<RawDocument> FetchAsync(FetchRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var stream = request.SinceModificationDate is null
            ? EnumerateViaSearchAsync(request, ct)
            : EnumerateViaDumpAsync(request.SinceModificationDate.Value, request, ct);

        var count = 0;
        await foreach (var doc in stream.WithCancellation(ct))
        {
            yield return doc;
            if (request.MaxItems is { } max && ++count >= max) yield break;
        }
    }

    // --- Ścieżka initial: enumeracja wycinka przez search API, potem pełny dokument po ID ---
    private async IAsyncEnumerable<RawDocument> EnumerateViaSearchAsync(FetchRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var page = ReadCheckpointPage();
        var emitted = 0;
        while (!ct.IsCancellationRequested)
        {
            var url = $"search/judgments?pageSize={_opt.PageSize}&pageNumber={page}" +
                      $"&courtType={_opt.CourtType}" +
                      // ccCourtType dotyczy tylko sądów powszechnych; pusty/biały = pomiń (sądy wyższe: SN/TK/KIO).
                      (!string.IsNullOrWhiteSpace(_opt.CcCourtType) ? $"&ccCourtType={_opt.CcCourtType}" : "") +
                      $"&judgmentDateFrom={_opt.JudgmentDateFrom}&judgmentDateTo={today}" +
                      "&sortingField=DATABASE_ID&sortingDirection=ASC";

            List<long> ids;
            int total;
            try
            {
                (ids, total) = await FetchSearchPageAsync(url, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Błąd pobrania strony search {Page} — kończę przebieg z tym, co mam.", page);
                yield break;
            }
            // Strona search pobrana poprawnie — checkpoint TERAZ, żeby wznowienie po awarii dalej
            // w pętli (np. na kolejnej stronie) nie musiało przewijać od strony 0.
            WriteCheckpointPage(page);
            if (ids.Count == 0) yield break;

            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();
                // Pomiń PRZED pobraniem pełnej treści (nie po — RawFetchRunner skip-checkuje dopiero po
                // ściągnięciu). Bez tego wznowienie przerwanego biegu (np. po TimeoutRejectedException na
                // stronie search — zob. komentarz przy FetchSearchPageAsync) zaczyna paginację od strony 0
                // i re-downloaduje KAŻDE już zapisane orzeczenie po pełną treść, tylko po to by je odrzucić.
                // Przy dużych typach sądów (COMMON: setki tysięcy) to marnowałoby godziny na próżno.
                if (await store.ExistsAsync(Source, id.ToString(CultureInfo.InvariantCulture), ct)) continue;
                var raw = await FetchFullByIdAsync(id, ct);
                if (raw is not null) yield return raw;
                if (request.MaxItems is { } max && ++emitted >= max) yield break;
            }

            page++;
            if ((long)page * _opt.PageSize >= total) yield break;
        }
    }

    private string CheckpointPath => Path.Combine(_rawStoreRoot, ".checkpoints", $"saos_{_opt.CourtType}.checkpoint");

    /// <summary>Ostatnia zapisana strona minus margines bezpieczeństwa (0, jeśli brak checkpointu lub błąd odczytu).</summary>
    private int ReadCheckpointPage()
    {
        try
        {
            var path = CheckpointPath;
            if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var page))
            {
                var resume = Math.Max(0, page - CheckpointOverlapPages);
                log.LogInformation("Checkpoint {CourtType}: ostatnia strona {Page} → wznawiam od strony {Resume}.",
                    _opt.CourtType, page, resume);
                return resume;
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Nie udało się odczytać checkpointu dla {CourtType} — zaczynam od strony 0.", _opt.CourtType);
        }
        return 0;
    }

    /// <summary>Zapisuje numer ostatnio poprawnie pobranej strony (atomowo, best-effort — błąd zapisu
    /// nie może przerwać fetchu, tylko oznacza droższe wznowienie).</summary>
    private void WriteCheckpointPage(int page)
    {
        try
        {
            var path = CheckpointPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, page.ToString(CultureInfo.InvariantCulture));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Nie udało się zapisać checkpointu strony {Page} dla {CourtType}.", page, _opt.CourtType);
        }
    }

    private async Task<(List<long> Ids, int Total)> FetchSearchPageAsync(string url, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(url, ct);
        var root = doc.RootElement;
        var total = root.GetProperty("info").GetProperty("totalResults").GetInt32();
        var ids = root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64()).ToList();
        return (ids, total);
    }

    private async Task<RawDocument?> FetchFullByIdAsync(long id, CancellationToken ct)
    {
        // Pojedynczy błąd pobrania (timeout/5xx) NIE może przerywać całego przebiegu — pomijamy dokument.
        try
        {
            using var doc = await GetJsonAsync($"judgments/{id}", ct);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
            var raw = BuildRawDocument(data);
            // Część orzeczeń SAOS ma tylko metadane, bez treści (puste textContent) — nie da się ich osadzić.
            // Pomijamy przy fetchu, by nie zaśmiecać magazynu i bazy 0-chunkowymi dokumentami (~2,5% zbioru).
            if (string.IsNullOrWhiteSpace(raw.RawContent))
            {
                log.LogInformation("Pomijam orzeczenie {Id} — brak treści (textContent puste w SAOS).", id);
                return null;
            }
            return raw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Pomijam orzeczenie {Id} (błąd pobrania).", id);
            return null;
        }
    }

    // --- Ścieżka incremental: dump API od daty modyfikacji + filtr wycinka po stronie klienta ---
    private async IAsyncEnumerable<RawDocument> EnumerateViaDumpAsync(
        DateTimeOffset since, FetchRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var sinceStr = since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var page = 0;
        while (!ct.IsCancellationRequested)
        {
            var url = $"dump/judgments?pageSize={_opt.PageSize}&pageNumber={page}&sinceModificationDate={sinceStr}";
            using var doc = await GetJsonAsync(url, ct);
            var items = doc.RootElement.GetProperty("items");
            if (items.GetArrayLength() == 0) yield break;

            foreach (var item in items.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                if (MatchesSlice(item, today)) yield return BuildRawDocument(item);
            }
            page++;
        }
    }

    /// <summary>Filtr wycinka dla ścieżki dump (dump nie ma filtra courtType).</summary>
    private bool MatchesSlice(JsonElement judgment, DateOnly today)
    {
        if (judgment.TryGetProperty("courtType", out var ctEl) &&
            !string.Equals(ctEl.GetString(), _opt.CourtType, StringComparison.OrdinalIgnoreCase))
            return false;

        // Poziom sądu apelacyjnego po nazwie sądu (dump nie zwraca ccCourtType).
        if (string.Equals(_opt.CcCourtType, "APPEAL", StringComparison.OrdinalIgnoreCase))
        {
            var courtName = judgment.TryGetProperty("division", out var div) &&
                            div.TryGetProperty("court", out var court) &&
                            court.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (courtName is null || !courtName.StartsWith("Sąd Apelacyjny", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Zakres dat (z odcięciem dat-śmieci w przyszłości).
        if (TryParseJudgmentDate(judgment, out var jd))
        {
            if (jd > today) return false;
            if (DateOnly.TryParse(_opt.JudgmentDateFrom, CultureInfo.InvariantCulture, out var from) && jd < from)
                return false;
        }
        return true;
    }

    private static bool TryParseJudgmentDate(JsonElement judgment, out DateOnly date)
    {
        date = default;
        return judgment.TryGetProperty("judgmentDate", out var d)
               && d.ValueKind == JsonValueKind.String
               && DateOnly.TryParse(d.GetString(), CultureInfo.InvariantCulture, out date);
    }

    private RawDocument BuildRawDocument(JsonElement judgment)
    {
        var id = judgment.GetProperty("id").GetInt64();
        var html = judgment.TryGetProperty("textContent", out var tc) ? tc.GetString() ?? "" : "";
        string? sourceUrl = judgment.TryGetProperty("source", out var src) &&
                            src.TryGetProperty("judgmentUrl", out var ju) ? ju.GetString() : null;
        sourceUrl ??= $"https://www.saos.org.pl/judgments/{id}";

        return new RawDocument
        {
            Source = SourceKeys.Saos,
            ExternalId = id.ToString(CultureInfo.InvariantCulture),
            DocType = DocTypes.Judgment,
            RawContent = html,
            SourceUrl = sourceUrl,
            SourceModificationDate = null, // SAOS nie zwraca per-item daty modyfikacji; checkpoint przez sync_state
            SourcePayload = judgment.Clone(),
        };
    }

    private async Task<JsonDocument> GetJsonAsync(string relativeUrl, CancellationToken ct)
    {
        using var resp = await http.GetAsync(relativeUrl, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            log.LogWarning("SAOS {Status} dla {Url}: {Body}", (int)resp.StatusCode, relativeUrl, body);
            resp.EnsureSuccessStatusCode();
        }
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }
}
