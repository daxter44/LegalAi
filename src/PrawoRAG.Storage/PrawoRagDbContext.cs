using Microsoft.EntityFrameworkCore;
using PrawoRAG.Domain;
using PrawoRAG.Storage.Entities;

namespace PrawoRAG.Storage;

public class PrawoRagDbContext(DbContextOptions<PrawoRagDbContext> options) : DbContext(options)
{
    /// <summary>
    /// Wymiar wektora. mmlw-base = 768, large-v2 = 1024. ZMIANA wymaga nowej migracji
    /// i re-embeddingu całego korpusu (model zablokowany na życie korpusu — zob. plan).
    /// </summary>
    public const int EmbeddingDimensions = 1024;

    /// <summary>
    /// Konfiguracja tekstowego słownika do tsvector. „simple" jest zawsze dostępny w stockowym
    /// obrazie Postgresa; „polish" wymaga zainstalowanego słownika — przełączyć po weryfikacji (0.2/3.1).
    /// </summary>
    public const string TextSearchConfig = "simple";

    public DbSet<DocumentEntity> Documents => Set<DocumentEntity>();
    public DbSet<ChunkEntity> Chunks => Set<ChunkEntity>();
    public DbSet<SyncStateEntity> SyncStates => Set<SyncStateEntity>();

    // --- warstwa demo (rozmowy + feedback) ---
    public DbSet<ConversationEntity> Conversations => Set<ConversationEntity>();
    public DbSet<MessageEntity> Messages => Set<MessageEntity>();
    public DbSet<FeedbackEntity> Feedbacks => Set<FeedbackEntity>();

    // --- analiza dokumentów (raport BEZ treści dokumentu — patrz AnalysisEntity) ---
    public DbSet<AnalysisEntity> Analyses => Set<AnalysisEntity>();
    public DbSet<AnalysisUnitEntity> AnalysisUnits => Set<AnalysisUnitEntity>();
    public DbSet<AnalysisUnitFeedbackEntity> AnalysisUnitFeedbacks => Set<AnalysisUnitFeedbackEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresExtension("vector");

        b.Entity<DocumentEntity>(e =>
        {
            e.ToTable("documents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.TypedMetadata).HasColumnType("jsonb");
            e.Property(x => x.QualityIssues).HasColumnType("text[]");
            // Klucz naturalny — fundament idempotencji (upsert nie duplikuje).
            e.HasIndex(x => new { x.Source, x.ExternalId }).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => new { x.Source, x.SourceModificationDate });
            e.HasIndex(x => x.CourtType);
            e.HasIndex(x => x.InForce);
            e.Property(x => x.CaseNumber).HasMaxLength(64);
            e.HasIndex(x => x.CaseNumber); // exact-match po sygnaturze (retrieval strukturalny)
        });

        b.Entity<ChunkEntity>(e =>
        {
            e.ToTable("chunks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Embedding).HasColumnType($"vector({EmbeddingDimensions})");
            e.Property(x => x.Locator).HasColumnType("jsonb");
            e.Property(x => x.EmbeddedWith).HasMaxLength(200);

            // tsvector generowany w bazie z kolumny Text (BM25).
            e.Property(x => x.SearchVector)
                .HasColumnType("tsvector")
                .HasComputedColumnSql($"to_tsvector('{TextSearchConfig}', coalesce(\"Text\", ''))", stored: true);

            e.HasOne(x => x.Document)
                .WithMany(d => d.Chunks)
                .HasForeignKey(x => x.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.DocumentId, x.ChunkIndex }).IsUnique();
            e.HasIndex(x => x.EmbeddedWith);
            e.Property(x => x.ArticleNo).HasMaxLength(16);
            e.HasIndex(x => x.ArticleNo); // dokładny filtr retrievalu strukturalnego (QU-1)
            // HNSW (cosine) dla retrieval gęstego; GIN dla BM25.
            e.HasIndex(x => x.Embedding).HasMethod("hnsw").HasOperators("vector_cosine_ops");
            e.HasIndex(x => x.SearchVector).HasMethod("gin");
        });

        b.Entity<SyncStateEntity>(e =>
        {
            e.ToTable("sync_state");
            e.HasKey(x => x.Source);
        });

        b.Entity<ConversationEntity>(e =>
        {
            e.ToTable("conversations");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(320); // długość adresu e-mail wg RFC
            e.Property(x => x.Title).HasMaxLength(300);
            e.HasIndex(x => new { x.UserId, x.UpdatedAt }); // lista własnych rozmów, najnowsze pierwsze
        });

        b.Entity<MessageEntity>(e =>
        {
            e.ToTable("messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Role).HasMaxLength(20);
            e.Property(x => x.RetrievedSources).HasColumnType("jsonb");
            e.Property(x => x.Model).HasMaxLength(200);
            e.HasOne(x => x.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ConversationId, x.CreatedAt });
            e.HasIndex(x => x.CreatedAt); // retencja: czyszczenie starszych niż 6 mies.
        });

        b.Entity<FeedbackEntity>(e =>
        {
            e.ToTable("feedback");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(320);
            e.Property(x => x.Verdict).HasMaxLength(30);
            e.Property(x => x.Note).HasMaxLength(2000);
            e.HasOne(x => x.Message)
                .WithOne(m => m.Feedback)
                .HasForeignKey<FeedbackEntity>(x => x.MessageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AnalysisEntity>(e =>
        {
            e.ToTable("analyses");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(320);
            e.Property(x => x.FileName).HasMaxLength(300);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.HasIndex(x => new { x.UserId, x.UpdatedAt }); // lista własnych analiz, najnowsze pierwsze
        });

        b.Entity<AnalysisUnitEntity>(e =>
        {
            e.ToTable("analysis_units");
            e.HasKey(x => x.Id);
            e.Property(x => x.Heading).HasMaxLength(200);
            e.Property(x => x.Verdict).HasMaxLength(20);
            e.Property(x => x.Sources).HasColumnType("jsonb");
            e.Property(x => x.Error).HasMaxLength(2000);
            e.HasOne(x => x.Analysis)
                .WithMany(a => a.Units)
                .HasForeignKey(x => x.AnalysisId)
                .OnDelete(DeleteBehavior.Cascade);
            // Klucz naturalny — retry jednostki nadpisuje wiersz (upsert), nie dubluje.
            e.HasIndex(x => new { x.AnalysisId, x.UnitIndex }).IsUnique();
        });

        b.Entity<AnalysisUnitFeedbackEntity>(e =>
        {
            e.ToTable("analysis_unit_feedback");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(320);
            e.Property(x => x.Verdict).HasMaxLength(30);
            e.Property(x => x.Note).HasMaxLength(2000);
            e.HasOne(x => x.Unit)
                .WithOne(u => u.Feedback)
                .HasForeignKey<AnalysisUnitFeedbackEntity>(x => x.AnalysisUnitId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
