using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace PrawoRAG.Storage;

public static class StorageServiceCollectionExtensions
{
    /// <summary>Rejestruje <see cref="PrawoRagDbContext"/> na Postgres+pgvector (z obsługą typu vector).</summary>
    public static IServiceCollection AddPrawoRagStorage(this IServiceCollection services, string connectionString)
    {
        // Diagnostyka: PRAWORAG_EF_SENSITIVE_LOGGING=1 odsłania wartości parametrów SQL w logu
        // (domyślnie EF Core maskuje je jako '?'). Opt-in i TYMCZASOWE — parametry zapytań to m.in.
        // treść pytań użytkowników, nie zostawiać włączone poza sesją debugowania.
        var sensitiveLogging = Environment.GetEnvironmentVariable("PRAWORAG_EF_SENSITIVE_LOGGING") == "1";
        services.AddDbContext<PrawoRagDbContext>(opt =>
        {
            opt.UseNpgsql(connectionString, o => o.UseVector());
            if (sensitiveLogging) opt.EnableSensitiveDataLogging();
        });
        return services;
    }
}
