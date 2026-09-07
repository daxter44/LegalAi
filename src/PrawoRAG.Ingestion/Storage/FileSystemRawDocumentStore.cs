using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain.Sources;

namespace PrawoRAG.Ingestion.Storage;

/// <summary>
/// Magazyn surowych na lokalnym dysku: jeden plik JSON per dokument w <c>{RootPath}/{source}/{externalId}.json</c>.
/// Przenośny (zip/rsync/gsutil między laptopem, M4 i GCP), inspekcjonowalny (jq/diff), bez migracji.
/// Zapis atomowy (tmp + rename). Nazwa pliku sanityzowana deterministycznie (ELI ma „/") — sam
/// <c>ExternalId</c> żyje w treści pliku i odtwarza się 1:1 przy enumeracji.
/// </summary>
public sealed class FileSystemRawDocumentStore(IOptions<RawStoreOptions> options) : IRawDocumentStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // Stały, niezależny od platformy zbiór znaków zakazanych — magazyn musi być identyczny na Windows i Linux (GCP).
    private static readonly char[] Reserved = "/\\:*?\"<>|".ToCharArray();

    private readonly string _root = options.Value.RootPath;

    public Task<bool> ExistsAsync(string source, string externalId, CancellationToken ct) =>
        Task.FromResult(File.Exists(PathFor(source, externalId)));

    public async Task SaveAsync(RawDocument document, CancellationToken ct)
    {
        var dir = SourceDir(document.Source);
        Directory.CreateDirectory(dir);

        var hash = Hashing.Sha256Hex(document.RawContent);
        var stored = StoredRawDocument.FromRaw(document, DateTimeOffset.UtcNow, hash);

        var finalPath = PathFor(document.Source, document.ExternalId);
        var tmpPath = finalPath + ".tmp";
        await using (var fs = File.Create(tmpPath))
            await JsonSerializer.SerializeAsync(fs, stored, Json, ct);
        File.Move(tmpPath, finalPath, overwrite: true); // atomowa publikacja — crash nie zostawia połówek
    }

    public async IAsyncEnumerable<RawDocument> EnumerateAsync(string source, [EnumeratorCancellation] CancellationToken ct)
    {
        var dir = SourceDir(source);
        if (!Directory.Exists(dir)) yield break;

        foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            StoredRawDocument? stored;
            await using (var fs = File.OpenRead(path))
                stored = await JsonSerializer.DeserializeAsync<StoredRawDocument>(fs, Json, ct);
            if (stored is not null) yield return stored.ToRaw();
        }
    }

    public async Task<RawDocument?> ReadAsync(string source, string externalId, CancellationToken ct)
    {
        var path = PathFor(source, externalId);
        if (!File.Exists(path)) return null;
        StoredRawDocument? stored;
        await using (var fs = File.OpenRead(path))
            stored = await JsonSerializer.DeserializeAsync<StoredRawDocument>(fs, Json, ct);
        // Weryfikacja tożsamości: nazwa pliku jest sanityzowana (ELI „/") — teoretyczna kolizja dwóch
        // różnych ExternalId do tej samej nazwy; prawdziwy id żyje w treści, więc potwierdzamy zgodność.
        var raw = stored?.ToRaw();
        return raw is not null && raw.ExternalId == externalId ? raw : null;
    }

    public Task<int> CountAsync(string source, CancellationToken ct)
    {
        var dir = SourceDir(source);
        var count = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.json").Count() : 0;
        return Task.FromResult(count);
    }

    private string SourceDir(string source) => Path.Combine(_root, Sanitize(source));
    private string PathFor(string source, string externalId) =>
        Path.Combine(SourceDir(source), Sanitize(externalId) + ".json");

    /// <summary>Zamienia znaki zakazane w nazwie pliku (ELI „DU/1997/553") na „_". Deterministyczna, cross-platform.</summary>
    private static string Sanitize(string id)
    {
        var chars = id.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (Array.IndexOf(Reserved, chars[i]) >= 0 || char.IsControl(chars[i]))
                chars[i] = '_';
        return new string(chars);
    }
}
