using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Sources;
using PrawoRAG.Ingestion;

// Jednorazowy przebieg ingestii (idealny pod smoke i pod harmonogram zewnętrzny: cron/systemd-timer
// na tanim VPS — bez procesu rezydentnego). Periodyczność = zewnętrzny scheduler wołający ten worker.
//
// Tryby (Ingestion:Mode, env Ingestion__Mode):
//   fetch         — pobierz surowe ze źródła do magazynu (idempotentnie), bez przetwarzania;
//   process       — przetwórz surowe z magazynu (OFFLINE) → baza; re-processing bez pobierania;
//   fetch-process — (DOMYŚLNY) pobierz, potem przetwórz; wstecznie kompatybilny build magazynu;
//   stream        — stara ścieżka: pobierz+przetwórz w pamięci, bez zapisu surowych na dysk.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddPrawoRagIngestion(builder.Configuration);

using var host = builder.Build();

var cfg = host.Services.GetRequiredService<IConfiguration>();
var source = cfg["Ingestion:Source"] ?? SourceKeys.Saos;
var maxItems = cfg.GetValue<int?>("Ingestion:MaxItems");
var mode = (cfg["Ingestion:Mode"] ?? "fetch-process").ToLowerInvariant();

switch (mode)
{
    case "stream":
    {
        var runner = host.Services.GetRequiredService<IngestionRunner>();
        var summary = await runner.RunAsync(source, new FetchRequest { MaxItems = maxItems }, default);
        Console.WriteLine($"INGEST DONE [stream {source}]: {summary}");
        break;
    }
    case "fetch":
    {
        var fetch = host.Services.GetRequiredService<RawFetchRunner>();
        var summary = await fetch.RunAsync(source, new FetchRequest { MaxItems = maxItems }, default);
        Console.WriteLine($"FETCH DONE [{source}]: {summary}");
        break;
    }
    case "process":
    {
        var process = host.Services.GetRequiredService<RawProcessRunner>();
        var summary = await process.RunAsync(source, maxItems, default);
        Console.WriteLine($"PROCESS DONE [{source}]: {summary}");
        break;
    }
    case "fetch-process":
    {
        var fetch = host.Services.GetRequiredService<RawFetchRunner>();
        var fetchSummary = await fetch.RunAsync(source, new FetchRequest { MaxItems = maxItems }, default);
        Console.WriteLine($"FETCH DONE [{source}]: {fetchSummary}");

        var process = host.Services.GetRequiredService<RawProcessRunner>();
        var procSummary = await process.RunAsync(source, maxItems, default);
        Console.WriteLine($"PROCESS DONE [{source}]: {procSummary}");
        break;
    }
    case "reprocess-failed":
    {
        // Celowany reprocessing dokumentów Failed (np. ISAP „za długie") — czyta po id z magazynu,
        // NIE enumeruje całości. Wypisuje rozkład powodów porażek i nową próbę. Uruchamiaj po naprawie
        // przyczyny albo na mocniejszej maszynie (GPU) — awarie przejściowe znikną, deterministyczne wrócą.
        var reprocess = host.Services.GetRequiredService<ReprocessFailedRunner>();
        var summary = await reprocess.RunAsync(source, maxItems, default);
        Console.WriteLine($"REPROCESS-FAILED DONE [{source}]: {summary}");
        break;
    }
    case "report":
    {
        // Raport jakości normalizacji (bez embeddingu, bez bazy) — ocena parsowania typów przed masowym pobraniem.
        var report = host.Services.GetRequiredService<QualityReportRunner>();
        await report.RunAsync(source, maxItems, default);
        break;
    }
    case "discover":
    {
        // Podgląd odkrywania aktów ELI (ile pasuje wg Eli:Discover) — BEZ pobierania. Poznaj wolumen zanim ruszysz.
        var eli = host.Services.GetRequiredService<PrawoRAG.Ingestion.Eli.EliSejmConnector>();
        var addrs = await eli.DiscoverAddressesAsync(default);
        Console.WriteLine($"\nODKRYTO {addrs.Count} aktów ELI (typ + akceptowany status; HTML lub PDF). Przykłady:");
        foreach (var a in addrs.Take(15)) Console.WriteLine($"  {a}");
        if (addrs.Count > 15) Console.WriteLine($"  … i {addrs.Count - 15} więcej");
        break;
    }
    case "sync-eli":
    {
        // AKT-5: dzienny delta-sync ELI. Discovery bieżącego rocznika (+opcjonalny lookback Eli:Sync:YearsBack);
        // RawFetchRunner pomija akty już w magazynie → pobiera TYLKO nowe pozycje (nowe ustawy/rozporządzenia,
        // w tym nowelizacje). Potem process (embed). Odpalać codziennie z crona/timera (jak SAOS).
        var eliOpt = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<PrawoRAG.Ingestion.Eli.EliOptions>>().Value;
        eliOpt.Discover.Enabled = true;
        eliOpt.Discover.YearTo = DateTime.UtcNow.Year;
        eliOpt.Discover.YearFrom = eliOpt.Discover.YearTo - (cfg.GetValue<int?>("Eli:Sync:YearsBack") ?? 0);
        Console.WriteLine($"SYNC-ELI: discovery {eliOpt.Discover.YearFrom}–{eliOpt.Discover.YearTo} (delta = pozycje spoza magazynu)");

        var syncFetch = host.Services.GetRequiredService<RawFetchRunner>();
        Console.WriteLine($"SYNC-ELI FETCH: {await syncFetch.RunAsync(SourceKeys.Eli, new FetchRequest { MaxItems = maxItems }, default)}");
        var syncProc = host.Services.GetRequiredService<RawProcessRunner>();
        Console.WriteLine($"SYNC-ELI PROCESS: {await syncProc.RunAsync(SourceKeys.Eli, maxItems, default)}");

        // AKT-5.2: relink — świeżo pobrana nowela nie odświeża listy `unabsorbedAmendments` aktu bazowego
        // przez fetch (skip-existing) ani process (treść bez zmian → skip). Relink dobiera SAME metadane
        // aktów bazowych z ELI i patchuje listę w bazie (bez re-embeddingu). Wyłączenie: Eli:Sync:Relink=false.
        if (cfg.GetValue<bool?>("Eli:Sync:Relink") != false)
        {
            var relink = host.Services.GetRequiredService<AmendmentRelinkRunner>();
            Console.WriteLine($"SYNC-ELI RELINK: {await relink.RunAsync(maxItems, default)}");
        }
        break;
    }
    default:
        throw new InvalidOperationException(
            $"Nieznany Ingestion:Mode '{mode}'. Dozwolone: fetch | process | fetch-process | stream | reprocess-failed | report | discover | sync-eli.");
}
