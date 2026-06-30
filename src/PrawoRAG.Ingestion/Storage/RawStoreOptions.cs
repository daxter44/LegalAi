namespace PrawoRAG.Ingestion.Storage;

/// <summary>Konfiguracja magazynu surowych dokumentów (sekcja „RawStore").</summary>
public sealed class RawStoreOptions
{
    public const string SectionName = "RawStore";

    /// <summary>Katalog główny magazynu. Surowe lądują w <c>{RootPath}/{source}/{externalId}.json</c>.
    /// Domyślnie względny <c>data/raw</c> (przenośny między maszynami: zip/rsync/gsutil).</summary>
    public string RootPath { get; set; } = "data/raw";
}
