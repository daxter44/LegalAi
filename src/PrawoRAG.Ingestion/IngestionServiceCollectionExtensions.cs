using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain.Documents;
using PrawoRAG.Domain.Sources;
using PrawoRAG.Embeddings;
using PrawoRAG.Ingestion.Chunking;
using PrawoRAG.Ingestion.Eli;
using PrawoRAG.Ingestion.Saos;
using PrawoRAG.Ingestion.Storage;
using PrawoRAG.Storage;

namespace PrawoRAG.Ingestion;

public static class IngestionServiceCollectionExtensions
{
    /// <summary>Rejestruje pełny tor ingestii: storage + embeddingi (TEI) + konektory + normalizery + chunker + pipeline + runner.</summary>
    public static IServiceCollection AddPrawoRagIngestion(this IServiceCollection services, IConfiguration config)
    {
        services.AddPrawoRagStorage(config.GetConnectionString("Db")
            ?? throw new InvalidOperationException("Brak ConnectionStrings:Db."));
        services.AddTeiEmbeddings(config);

        services.Configure<SaosOptions>(config.GetSection(SaosOptions.SectionName));
        services.Configure<ChunkerOptions>(config.GetSection(ChunkerOptions.SectionName));
        services.Configure<RawStoreOptions>(config.GetSection(RawStoreOptions.SectionName));

        // Konektor SAOS jako typowany HttpClient z resilience; eksponowany jako ISourceConnector.
        // Timeoutami rządzi resilience handler (HttpClient.Timeout = nieskończony, by się nie nakładały).
        services.AddHttpClient<SaosConnector>((sp, c) =>
        {
            var opt = sp.GetRequiredService<IOptions<SaosOptions>>().Value;
            c.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
            c.Timeout = Timeout.InfiniteTimeSpan;
            c.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        }).AddStandardResilienceHandler(o =>
        {
            // Search SAOS z filtrami bywa wolny (~8–15s) — domyślne 10s na próbę to za mało.
            var attempt = TimeSpan.FromSeconds(
                config.GetValue<int?>($"{SaosOptions.SectionName}:AttemptTimeoutSeconds") ?? 45);
            o.AttemptTimeout.Timeout = attempt;
            o.TotalRequestTimeout.Timeout = attempt * 2 + TimeSpan.FromSeconds(30); // > attempt; mieści retry
            o.CircuitBreaker.SamplingDuration = attempt * 2;                          // wymóg: >= 2× AttemptTimeout
        });
        services.AddTransient<ISourceConnector>(sp => sp.GetRequiredService<SaosConnector>());

        // Konektor ELI/Sejm (akty prawne) — analogicznie do SAOS.
        services.Configure<EliOptions>(config.GetSection(EliOptions.SectionName));
        services.AddHttpClient<EliSejmConnector>((sp, c) =>
        {
            var opt = sp.GetRequiredService<IOptions<EliOptions>>().Value;
            c.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
            c.Timeout = Timeout.InfiniteTimeSpan;
            c.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        }).AddStandardResilienceHandler(o =>
        {
            var attempt = TimeSpan.FromSeconds(
                config.GetValue<int?>($"{EliOptions.SectionName}:AttemptTimeoutSeconds") ?? 45);
            o.AttemptTimeout.Timeout = attempt;
            o.TotalRequestTimeout.Timeout = attempt * 2 + TimeSpan.FromSeconds(30);
            o.CircuitBreaker.SamplingDuration = attempt * 2;
        });
        services.AddTransient<ISourceConnector>(sp => sp.GetRequiredService<EliSejmConnector>());

        services.AddSingleton<IDocumentNormalizer, JudgmentNormalizer>();
        services.AddSingleton<IDocumentNormalizer, ActNormalizer>();
        services.AddTransient<IChunker, TokenAwareChunker>();
        services.AddScoped<IngestionPipeline>();
        services.AddSingleton<IngestionRunner>();

        // Magazyn surowych + dwie fazy (fetch / process).
        services.AddSingleton<IRawDocumentStore, FileSystemRawDocumentStore>();
        services.AddSingleton<RawFetchRunner>();
        services.AddSingleton<RawProcessRunner>();
        return services;
    }
}
