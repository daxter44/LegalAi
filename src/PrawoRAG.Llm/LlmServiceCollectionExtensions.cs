using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain.Llm;

namespace PrawoRAG.Llm;

public static class LlmServiceCollectionExtensions
{
    /// <summary>
    /// Rejestruje <see cref="ILlmProvider"/> wg <c>Llm:Provider</c>: <c>claude</c> (domyślnie, cloud)
    /// albo <c>local</c> (serwer zgodny z OpenAI: Ollama/llama.cpp — pakiet Diamond, dane nie wychodzą).
    /// </summary>
    public static IServiceCollection AddPrawoRagLlm(this IServiceCollection services, IConfiguration config)
    {
        var provider = (config["Llm:Provider"] ?? "claude").ToLowerInvariant();
        return provider switch
        {
            "local" or "ollama" or "openai-compatible" => services.AddLocalLlm(config),
            "claude" or "anthropic" => services.AddClaudeLlm(config),
            _ => throw new InvalidOperationException(
                $"Nieznany Llm:Provider '{provider}'. Dozwolone: claude | local."),
        };
    }

    /// <summary>Rejestruje lokalny <see cref="ILlmProvider"/> (OpenAI-compatible: Ollama/llama.cpp). Sekcja Llm:Local.</summary>
    public static IServiceCollection AddLocalLlm(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<LocalLlmOptions>(config.GetSection(LocalLlmOptions.SectionName));
        services.AddHttpClient<ILlmProvider, OpenAiCompatibleLlmProvider>((sp, c) =>
        {
            var opt = sp.GetRequiredService<IOptions<LocalLlmOptions>>().Value;
            c.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
            c.Timeout = Timeout.InfiniteTimeSpan; // lokalna generacja bywa wolna — nie ucinamy strumienia
        });
        return services;
    }

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
