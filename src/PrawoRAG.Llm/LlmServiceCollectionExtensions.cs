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
        // Model pomocniczy jest rejestrowany ZAWSZE i niezależnie od wyboru modelu głównego —
        // korzystają z niego router intencji i przeformułowanie zapytań (Zadania 7/11). Sama
        // rejestracja nie wykonuje żadnego żądania, więc brak serwera pomocniczego niczego nie psuje:
        // każde nieudane wywołanie degraduje w stronę retrievalu (decyzja przekrojowa 3 planu ROU).
        services.AddAuxLlm(config);

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

    /// <summary>
    /// Klucz DI modelu POMOCNICZEGO (router intencji, przeformułowanie zapytań). Usługa kluczowana,
    /// bo typowana rejestracja <c>AddHttpClient&lt;ILlmProvider, …&gt;</c> jest już zajęta przez model
    /// odpowiadający — a to musi być DRUGA instancja tego samego interfejsu, z innym modelem
    /// i innym (skończonym) timeoutem.
    /// </summary>
    public const string AuxProviderKey = "aux";

    /// <summary>
    /// Rejestruje model pomocniczy pod kluczem <see cref="AuxProviderKey"/> (Zadanie 5 planu ROU).
    /// Zawsze OpenAI-compatible — również gdy model główny to Claude, bo zadania pomocnicze mają
    /// zostać lokalne/tanie i nie wychodzić do chmury.
    /// </summary>
    public static IServiceCollection AddAuxLlm(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AuxLlmOptions>(config.GetSection(AuxLlmOptions.SectionName));
        services.AddHttpClient(AuxProviderKey, (sp, c) =>
        {
            var opt = sp.GetRequiredService<IOptions<AuxLlmOptions>>().Value;
            c.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
            // Świadomie SKOŃCZONY, w odróżnieniu od klienta modelu głównego: model pomocniczy ma być
            // szybki albo żaden — każdy jego brak degraduje w stronę retrievalu, nie w stronę błędu.
            c.Timeout = TimeSpan.FromSeconds(opt.TimeoutSeconds);
        });
        services.AddKeyedSingleton<ILlmProvider>(AuxProviderKey, (sp, _) =>
        {
            var opt = sp.GetRequiredService<IOptions<AuxLlmOptions>>().Value;
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(AuxProviderKey);
            // Przekładamy AuxLlmOptions na LocalLlmOptions, bo provider OpenAI-compat jest ten sam —
            // różni się wyłącznie konfiguracją (model, limit tokenów, klucz).
            return new OpenAiCompatibleLlmProvider(http, Options.Create(new LocalLlmOptions
            {
                BaseUrl = opt.BaseUrl,
                Model = opt.Model,
                ApiKey = opt.ApiKey,
                MaxTokens = opt.MaxTokens,
            }));
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
