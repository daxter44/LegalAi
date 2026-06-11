using System.Security.Cryptography;
using System.Text;

namespace PrawoRAG.Ingestion;

/// <summary>Wspólny hash treści (SHA-256 hex). Używany i przez normalizer, i przez pipeline,
/// by porównanie „przed normalizacją" było spójne (skip dokumentów niezmienionych).</summary>
public static class Hashing
{
    public static string Sha256Hex(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
