using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace PrawoRAG.Storage;

public static class StorageServiceCollectionExtensions
{
    /// <summary>Rejestruje <see cref="PrawoRagDbContext"/> na Postgres+pgvector (z obsługą typu vector).</summary>
    public static IServiceCollection AddPrawoRagStorage(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<PrawoRagDbContext>(opt =>
            opt.UseNpgsql(connectionString, o => o.UseVector()));
        return services;
    }
}
