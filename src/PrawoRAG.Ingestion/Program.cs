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
        Console.WriteLine($"\nODKRYTO {addrs.Count} aktów ELI (typ + status obowiązujący + tekst HTML). Przykłady:");
        foreach (var a in addrs.Take(15)) Console.WriteLine($"  {a}");
        if (addrs.Count > 15) Console.WriteLine($"  … i {addrs.Count - 15} więcej");
        break;
    }
    default:
        throw new InvalidOperationException(
            $"Nieznany Ingestion:Mode '{mode}'. Dozwolone: fetch | process | fetch-process | stream | report.");
}
