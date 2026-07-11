using System.Text.Json;
using PrawoRAG.Domain.Sources;

namespace PrawoRAG.Ingestion;

/// <summary>
/// Raport porażek runa „process" (ODP-3): jeden JSON na linię, dopisywany i flushowany od razu
/// (crash-safe). Trzyma PEŁNY wyjątek (stack + inner) — konsola po nocnym runie znika,
/// a <c>FailureReason</c> w DB jest ucięty do 1000 znaków; ten plik odpowiada na
/// „co dokładnie stało się z dokumentem nr 59584" bez ponownego uruchamiania.
/// Tworzony leniwie przy pierwszej porażce — czyste runy nie zostawiają pustych plików.
/// </summary>
public sealed class FailureReport(string filePath)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // czytelne polskie znaki w jq/grep
    };

    public string FilePath { get; } = filePath;

    public static FailureReport Create(string dir, string source)
    {
        Directory.CreateDirectory(dir);
        var name = $"process-failures-{Sanitize(source)}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.jsonl";
        return new FailureReport(Path.Combine(dir, name));
    }

    public void Write(int seq, RawDocument raw, string? stage, Exception? error)
    {
        var line = JsonSerializer.Serialize(new
        {
            seq,
            source = raw.Source,
            externalId = raw.ExternalId,
            docType = raw.DocType,
            stage,
            error = error?.ToString(),
            at = DateTimeOffset.UtcNow,
        }, Json);
        File.AppendAllText(FilePath, line + Environment.NewLine);
    }

    private static string Sanitize(string s) =>
        string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}
