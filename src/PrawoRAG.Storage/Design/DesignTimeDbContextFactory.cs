using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PrawoRAG.Storage.Design;

/// <summary>
/// Fabryka używana TYLKO przez narzędzia EF (`dotnet ef migrations`/`database update`).
/// Connection string z env PRAWORAG_DB lub domyślny lokalny (Podman/compose).
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PrawoRagDbContext>
{
    public PrawoRagDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("PRAWORAG_DB")
                   ?? "Host=localhost;Port=5432;Database=praworag;Username=praworag;Password=praworag";

        var options = new DbContextOptionsBuilder<PrawoRagDbContext>()
            .UseNpgsql(conn, o => o.UseVector())
            .Options;

        return new PrawoRagDbContext(options);
    }
}
