using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain.Embeddings;

namespace PrawoRAG.Embeddings;

public static class EmbeddingsServiceCollectionExtensions
{
    /// <summary>Rejestruje <see cref="IEmbeddingProvider"/> = TEI, z typowanym HttpClient (BaseAddress z opcji).</summary>
    public static IServiceCollection AddTeiEmbeddings(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<TeiOptions>(config.GetSection(TeiOptions.SectionName));
        services.AddHttpClient<IEmbeddingProvider, TeiEmbeddingProvider>((sp, client) =>
        {
            var opt = sp.GetRequiredService<IOptions<TeiOptions>>().Value;
            client.BaseAddress = new Uri(opt.BaseUrl);
            client.Timeout = TimeSpan.FromMinutes(5); // batch embeddingów może trwać
        });
        return services;
    }
}
