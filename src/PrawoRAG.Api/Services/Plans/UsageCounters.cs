using Microsoft.EntityFrameworkCore;
using PrawoRAG.Storage;

namespace PrawoRAG.Api.Services.Plans;

/// <summary>Nazwy liczników. Osobne przestrzenie = osobne osie, nie da się ich pomylić w SQL-u.</summary>
public static class UsageScopes
{
    /// <summary>Zapytania w okresie rozliczeniowym konta (oś planu) — czat.</summary>
    public const string UserRequestsPeriod = "user_requests";

    /// <summary>Analizy dokumentów w okresie rozliczeniowym konta (oś planu) — per DOKUMENT.</summary>
    public const string UserAnalysesPeriod = "user_analyses";

    /// <summary>Zapytania na dobę per użytkownik — tryb bez kont (bramka invite), zachowanie z alfy.</summary>
    public const string UserRequestsDay = "user_requests_day";

    /// <summary>Zapytania na dobę łącznie (oś pojemności).</summary>
    public const string GlobalRequestsDay = "global_requests_day";

    /// <summary>Znaki wyjścia LLM na dobę łącznie (oś pojemności).</summary>
    public const string GlobalCharsDay = "global_chars_day";
}

/// <summary>
/// Magazyn liczników zużycia. Wydzielony za interfejs, bo <see cref="CostGuard"/> ma dwie warstwy
/// wartości: REGUŁY (dwie osie, kolejność, zwrot rezerwacji) i SKŁADOWANIE. Reguły chcemy sprawdzać
/// testami jednostkowymi bez bazy; składowanie — na żywym Postgresie, bo tylko on dowiedzie
/// atomowości.
/// </summary>
public interface IUsageCounters
{
    /// <summary>
    /// Zwiększa licznik o 1 tylko wtedy, gdy wynik zmieści się w limicie. Zwraca nową wartość albo
    /// <c>null</c>, gdy limit padł. Implementacja MUSI być atomowa — dwa równoległe zapytania tego
    /// samego użytkownika nie mogą przepchnąć się ponad limit.
    /// </summary>
    Task<long?> TryIncrementAsync(string scope, string key, DateOnly period, long limit, CancellationToken ct = default);

    /// <summary>Bezwarunkowa zmiana (doliczenie znaków, zwrot rezerwacji). Nie schodzi poniżej zera.</summary>
    Task AddAsync(string scope, string key, DateOnly period, long delta, CancellationToken ct = default);

    Task<long> CurrentAsync(string scope, string key, DateOnly period, CancellationToken ct = default);
}

/// <summary>
/// Liczniki w Postgresie. Cała decyzja „czy mieści się w limicie" zapada w JEDNYM zapytaniu —
/// warunkowy upsert. Naiwne „odczytaj, sprawdź, zapisz" przepuszcza nadmiar, gdy użytkownik ma
/// otwarte dwie karty; to jest dokładnie ten błąd, którego ten kształt SQL-a unika.
/// </summary>
public sealed class PostgresUsageCounters(IServiceScopeFactory scopes) : IUsageCounters
{
    public async Task<long?> TryIncrementAsync(
        string scope, string key, DateOnly period, long limit, CancellationToken ct = default)
    {
        if (limit <= 0) return null;

        const string sql = """
            INSERT INTO usage_counters ("Scope", "Key", "PeriodStart", "Value")
            VALUES (@scope, @key, @period, 1)
            ON CONFLICT ("Scope", "Key", "PeriodStart") DO UPDATE
                SET "Value" = usage_counters."Value" + 1
                WHERE usage_counters."Value" < @limit
            RETURNING "Value";
            """;

        await using var dbScope = scopes.CreateAsyncScope();
        var db = dbScope.ServiceProvider.GetRequiredService<PrawoRagDbContext>();
        var conn = await OpenAsync(db, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        AddParams(cmd, scope, key, period);
        Add(cmd, "limit", limit);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    public async Task AddAsync(
        string scope, string key, DateOnly period, long delta, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO usage_counters ("Scope", "Key", "PeriodStart", "Value")
            VALUES (@scope, @key, @period, GREATEST(@delta, 0))
            ON CONFLICT ("Scope", "Key", "PeriodStart") DO UPDATE
                SET "Value" = GREATEST(usage_counters."Value" + @delta, 0);
            """;

        await using var dbScope = scopes.CreateAsyncScope();
        var db = dbScope.ServiceProvider.GetRequiredService<PrawoRagDbContext>();
        var conn = await OpenAsync(db, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        AddParams(cmd, scope, key, period);
        Add(cmd, "delta", delta);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> CurrentAsync(
        string scope, string key, DateOnly period, CancellationToken ct = default)
    {
        await using var dbScope = scopes.CreateAsyncScope();
        var db = dbScope.ServiceProvider.GetRequiredService<PrawoRagDbContext>();
        return await db.UsageCounters
            .Where(c => c.Scope == scope && c.Key == key && c.PeriodStart == period)
            .Select(c => c.Value)
            .FirstOrDefaultAsync(ct);
    }

    private static async Task<System.Data.Common.DbConnection> OpenAsync(PrawoRagDbContext db, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        return conn;
    }

    private static void AddParams(System.Data.Common.DbCommand cmd, string scope, string key, DateOnly period)
    {
        Add(cmd, "scope", scope);
        Add(cmd, "key", key);
        Add(cmd, "period", period);
    }

    /// <summary>Parametr przez fabrykę połączenia — typ wnioskuje sterownik, my nie wiążemy się z jego API.</summary>
    private static void Add(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
