using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain.Documents;
using PrawoRAG.Domain.Sources;
using PrawoRAG.Embeddings;
using PrawoRAG.Ingestion.Chunking;
using PrawoRAG.Ingestion.Saos;
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

        // Konektor SAOS jako typowany HttpClient z resilience; eksponowany jako ISourceConnector.
        services.AddHttpClient<SaosConnector>((sp, c) =>
        {
            var opt = sp.GetRequiredService<IOptions<SaosOptions>>().Value;
            c.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromSeconds(60);
            c.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        }).AddStandardResilienceHandler();
        services.AddTransient<ISourceConnector>(sp => sp.GetRequiredService<SaosConnector>());

        services.AddSingleton<IDocumentNormalizer, JudgmentNormalizer>();
        services.AddTransient<IChunker, TokenAwareChunker>();
        services.AddScoped<IngestionPipeline>();
        services.AddSingleton<IngestionRunner>();
        return services;
    }
}
