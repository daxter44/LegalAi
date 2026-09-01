using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Sources;
using PrawoRAG.Ingestion;
using PrawoRAG.Ingestion.EurLex;
using PrawoRAG.Storage;

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
        if (string.Equals(source, SourceKeys.EurLex, StringComparison.OrdinalIgnoreCase))
        {
            // Prawo UE — Faza 1: wolumen i SKŁAD zakresu bez pobierania treści. Raport odpowiada na pytanie
            // „ile z tego niesie własną treść, a ile jest instrukcją zmiany" (zmierzone na populacji:
            // 52% aktów obowiązujących tylko zmienia inne akty, a 91% z nich jest już wchłonięte w konsolidacje).
            var eurLexOpt = host.Services
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<PrawoRAG.Ingestion.EurLex.EurLexOptions>>().Value;
            eurLexOpt.Discover.Enabled = true;
            var discovery = host.Services.GetRequiredService<PrawoRAG.Ingestion.EurLex.EurLexDiscovery>();
            var acts = await discovery.DiscoverAsync(default);

            Console.WriteLine($"\nODKRYTO {acts.Count} aktów UE "
                + $"({string.Join("+", eurLexOpt.Discover.ResourceTypes)}, obowiązujące: {eurLexOpt.Discover.InForceOnly}, "
                + $"lata {eurLexOpt.Discover.YearFrom}–{eurLexOpt.Discover.YearTo}).\n");
            Console.WriteLine("Skład po klasie aktu (decyduje, czy akt wchodzi do wektorów):");
            foreach (var g in acts.GroupBy(a => a.Class).OrderByDescending(g => g.Count()))
                Console.WriteLine($"  {g.Key.ToMetadataValue(),-18} {g.Count(),6}  {(g.Key.CarriesOwnText() ? "treść + chunki" : "tylko metadane")}");
            Console.WriteLine($"\nDo chunkowania: {acts.Count(a => a.Class.CarriesOwnText())}; "
                + $"metadane-only: {acts.Count(a => !a.Class.CarriesOwnText())}.");
            Console.WriteLine($"Z własną wersją skonsolidowaną: {acts.Count(a => a.Consolidations.Count > 0)}.");
            Console.WriteLine("\nPrzykłady (CELEX, klasa, kandydaci treści):");
            foreach (var a in acts.Take(10))
                Console.WriteLine($"  {a.Celex,-12} {a.Class.ToMetadataValue(),-18} {string.Join(" → ", a.TextCandidates(DateOnly.FromDateTime(DateTime.UtcNow)))}");
            break;
        }

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
    case "reprocess-ustepy":
    {
        // Wymuszony reprocessing ustaw pod podział na ustępy (unit_pass w ActNormalizer, diagnoza
        // 2026-08-31; pilot: art. 11 ochrony lokatorów #512 -> #8). Cele dobierane automatycznie
        // (akty ELI/HTML z długim chunkiem artykułowym bez ustępu), wznawialny przez checkpoint.
        // Konfiguracja: Reprocess:CheckpointFile (domyślnie logs/reprocess-ustepy.done),
        // Reprocess:DelayMs (domyślnie 250 — grzeczność wobec api.sejm.gov.pl),
        // Ingestion:MaxItems = limit aktów w TYM biegu (smoke na małej porcji).
        var checkpoint = cfg["Reprocess:CheckpointFile"] ?? Path.Combine("logs", "reprocess-ustepy.done");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(checkpoint))!);
        var delayMs = cfg.GetValue<int?>("Reprocess:DelayMs") ?? 250;

        var ustep = host.Services.GetRequiredService<UstepReprocessRunner>();
        var summary = await ustep.RunAsync(checkpoint, maxItems, delayMs, default);
        Console.WriteLine($"REPROCESS-USTEPY DONE: {summary}");
        break;
    }
    case "relink":
    {
        // Samodzielny relink (bez fetchu/procesu nowych aktów — patrz sync-eli): odświeża listy
        // unabsorbedAmendments aktów bazowych (od 2026-09-01 z warunkiem vacatio legis — data
        // obwieszczenia t.j.) i na końcu przelicza flagi wchłoniętych nowel. Do runbooków.
        var relinkRunner = host.Services.GetRequiredService<AmendmentRelinkRunner>();
        Console.WriteLine($"RELINK: {await relinkRunner.RunAsync(maxItems, default)}");
        break;
    }
    case "absorbed-flags":
    {
        // Backfill/przeliczenie flagi documents.AbsorbedAmendment (wchłonięte nowelizacje poza torami
        // semantycznymi retrievalu — ANALIZA-NADGODZINY-WCHLONIETE-NOWELE-POMIAR-2026-09-01). Jeden
        // zbiorczy UPDATE, idempotentny, bez sieci i bez re-embeddingu; w stanie ustalonym to samo
        // przeliczenie biegnie automatycznie na końcu relinku (sync-eli).
        using var scope = host.Services.CreateScope();
        var flagDb = scope.ServiceProvider.GetRequiredService<PrawoRagDbContext>();
        var changed = await AmendmentRelinkRunner.RecomputeAbsorbedFlagsAsync(flagDb, default);
        Console.WriteLine($"ABSORBED-FLAGS DONE: changed={changed}");
        break;
    }
    case "backfill-noise":
    {
        // Backfill jakości treści chunków (PLAN-NAPRAWA-SZUMU-CHUNKOW-2026-08-28.md): mojibake ze starych
        // PDF-ów Dz.U., przypisy historii nowelizacji, markery list „⚫". Czyszczenie + TokenCount +
        // re-embedding TYLKO dotkniętych chunków (~74 tys. = 0,9% korpusu). Oryginały → chunk_noise_backup.
        // Konfiguracja: Backfill:Problems (csv, domyślnie wszystkie), Backfill:DryRun, Backfill:BatchSize,
        // Backfill:MaxChunks jako limit chunków do prób na małej porcji (celowo NIE Ingestion:MaxItems —
        // appsettings ustawia je na 3 dla zwykłego ingestu i po cichu ucinałoby backfill po jednej partii).
        var problems = (cfg["Backfill:Problems"] ?? "mojibake,footnotes,bullets")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var dryRun = cfg.GetValue<bool?>("Backfill:DryRun") ?? false;
        var batchSize = cfg.GetValue<int?>("Backfill:BatchSize") ?? 200;
        var maxChunks = cfg.GetValue<int?>("Backfill:MaxChunks");

        var backfill = host.Services.GetRequiredService<NoiseBackfillRunner>();
        var results = await backfill.RunAsync(problems, dryRun, batchSize, maxChunks, default);
        foreach (var r in results)
            Console.WriteLine($"BACKFILL-NOISE {(dryRun ? "DRY-RUN " : "")}DONE: {r}");
        break;
    }
    default:
        throw new InvalidOperationException(
            $"Nieznany Ingestion:Mode '{mode}'. Dozwolone: fetch | process | fetch-process | stream | reprocess-failed | report | discover | sync-eli | backfill-noise | reprocess-ustepy.");
}
