using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Sources;
using PrawoRAG.Ingestion;

// Jednorazowy przebieg ingestii (idealny pod smoke i pod harmonogram zewnętrzny: cron/systemd-timer
// na tanim VPS — bez procesu rezydentnego). Periodyczność = zewnętrzny scheduler wołający ten worker.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddPrawoRagIngestion(builder.Configuration);

using var host = builder.Build();

var runner = host.Services.GetRequiredService<IngestionRunner>();
var cfg = host.Services.GetRequiredService<IConfiguration>();
var source = cfg["Ingestion:Source"] ?? SourceKeys.Saos;
var maxItems = cfg.GetValue<int?>("Ingestion:MaxItems");

var summary = await runner.RunAsync(source, new FetchRequest { MaxItems = maxItems }, default);
Console.WriteLine($"INGEST DONE [{source}]: {summary}");
