using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain.Llm;

namespace PrawoRAG.Llm;

public static class LlmServiceCollectionExtensions
{
    /// <summary>Rejestruje <see cref="ILlmProvider"/> = Claude. Klucz API: konfiguracja Llm:Claude:ApiKey lub env ANTHROPIC_API_KEY.</summary>
    public static IServiceCollection AddClaudeLlm(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ClaudeOptions>(config.GetSection(ClaudeOptions.SectionName));
        services.PostConfigure<ClaudeOptions>(o =>
            o.ApiKey ??= Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));

        services.AddHttpClient<ILlmProvider, ClaudeLlmProvider>((sp, c) =>
        {
            var opt = sp.GetRequiredService<IOptions<ClaudeOptions>>().Value;
            c.BaseAddress = new Uri(opt.BaseUrl);
            c.Timeout = TimeSpan.FromSeconds(120);
        });
        return services;
    }
}
