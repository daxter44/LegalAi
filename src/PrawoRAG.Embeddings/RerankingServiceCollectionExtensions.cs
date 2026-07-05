using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain.Retrieval;

namespace PrawoRAG.Embeddings;

public static class RerankingServiceCollectionExtensions
{
    /// <summary>
    /// Rejestruje <see cref="IReranker"/> = TEI TYLKO gdy <c>Reranker:Enabled=true</c>. Gdy wyłączony —
    /// nic nie rejestruje, a <c>HybridRetriever</c> (opcjonalny reranker = null) działa jak dotąd.
    /// </summary>
    public static IServiceCollection AddTeiReranker(this IServiceCollection services, IConfiguration config)
    {
        var section = config.GetSection(RerankerOptions.SectionName);
        if (!section.GetValue<bool>("Enabled")) return services;

        services.Configure<RerankerOptions>(section);
        services.AddHttpClient<IReranker, TeiReranker>((sp, client) =>
        {
            var opt = sp.GetRequiredService<IOptions<RerankerOptions>>().Value;
            client.BaseAddress = new Uri(opt.BaseUrl);
            client.Timeout = TimeSpan.FromMinutes(2);
        });
        return services;
    }
}
