using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain.Llm;
using PrawoRAG.Llm;

namespace PrawoRAG.Tests.Llm;

/// <summary>
/// T-AUX (Zadanie 5 planu ROU) — rejestracja modelu POMOCNICZEGO (router intencji, przeformułowanie
/// zapytań). Kluczowa usługa, bo typowana rejestracja <c>ILlmProvider</c> jest już zajęta przez model
/// odpowiadający. Testy pilnują trzech rzeczy, na których stoi cała Faza 2/4:
/// (a) model pomocniczy da się rozwiązać OBOK modelu głównego i to są DWIE różne instancje,
/// (b) jego timeout jest SKOŃCZONY (klient modelu głównego ma nieskończony — tu byłby to błąd),
/// (c) brak sekcji konfiguracji nie wywala aplikacji.
/// </summary>
public class AuxLlmRegistrationTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddPrawoRagLlm(config);
        return services.BuildServiceProvider();
    }

    [Fact] // Model pomocniczy obok glownego: dwie rozne instancje, dwa rozne modele.
    public void Aux_and_main_providers_coexist()
    {
        using var sp = Build(new()
        {
            ["Llm:Provider"] = "local",
            ["Llm:Local:Model"] = "gemma-4-31b-it",
            ["Llm:Aux:Model"] = "bielik-11b",
        });

        var main = sp.GetRequiredService<ILlmProvider>();
        var aux = sp.GetRequiredKeyedService<ILlmProvider>(LlmServiceCollectionExtensions.AuxProviderKey);

        Assert.Equal("gemma-4-31b-it", main.ModelId);
        Assert.Equal("bielik-11b", aux.ModelId);
        Assert.NotSame(main, aux);
    }

    [Fact] // Model pomocniczy jest dostepny TAKZE gdy glowny to Claude - zadania sluzebne zostaja lokalne.
    public void Aux_is_registered_even_with_claude_main()
    {
        using var sp = Build(new()
        {
            ["Llm:Provider"] = "claude",
            ["Llm:Claude:ApiKey"] = "test-key",
            ["Llm:Aux:Model"] = "bielik-11b",
        });

        var aux = sp.GetRequiredKeyedService<ILlmProvider>(LlmServiceCollectionExtensions.AuxProviderKey);
        Assert.Equal("bielik-11b", aux.ModelId);
    }

    [Fact] // Timeout SKONCZONY i konfigurowalny - inaczej padniecie modelu pomocniczego wiesiloby cala ture.
    public void Aux_http_client_has_finite_configured_timeout()
    {
        using var sp = Build(new()
        {
            ["Llm:Provider"] = "local",
            ["Llm:Aux:TimeoutSeconds"] = "7",
        });

        var client = sp.GetRequiredService<IHttpClientFactory>()
            .CreateClient(LlmServiceCollectionExtensions.AuxProviderKey);

        Assert.Equal(TimeSpan.FromSeconds(7), client.Timeout);
        Assert.NotEqual(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    [Fact] // Brak sekcji Llm:Aux => wartosci domyslne (Bielik 11B, 10 s), zero wyjatku na starcie.
    public void Missing_aux_section_falls_back_to_defaults()
    {
        using var sp = Build(new() { ["Llm:Provider"] = "local" });

        var opt = sp.GetRequiredService<IOptions<AuxLlmOptions>>().Value;
        Assert.Equal(10, opt.TimeoutSeconds);
        Assert.Equal(256, opt.MaxTokens); // niski limit = siłowe ucięcie „myślenia" (reguła R2 planu)
        Assert.Contains("bielik", opt.Model, StringComparison.OrdinalIgnoreCase);

        var aux = sp.GetRequiredKeyedService<ILlmProvider>(LlmServiceCollectionExtensions.AuxProviderKey);
        Assert.NotNull(aux);
    }

    [Fact] // Klient pomocniczy celuje w SWOJ BaseUrl, nie w adres modelu glownego.
    public void Aux_client_uses_its_own_base_url()
    {
        using var sp = Build(new()
        {
            ["Llm:Provider"] = "local",
            ["Llm:Local:BaseUrl"] = "http://main-host:9000/v1",
            ["Llm:Aux:BaseUrl"] = "http://aux-host:11434/v1",
        });

        var client = sp.GetRequiredService<IHttpClientFactory>()
            .CreateClient(LlmServiceCollectionExtensions.AuxProviderKey);

        Assert.Equal("http://aux-host:11434/v1/", client.BaseAddress!.ToString());
    }
}
