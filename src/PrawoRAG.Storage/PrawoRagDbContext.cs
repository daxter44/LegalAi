using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using PrawoRAG.Domain;
using PrawoRAG.Storage.Entities;

namespace PrawoRAG.Storage;

/// <summary>
/// Jeden kontekst na całą bazę: korpus, rozmowy i konta (E1/T-1). Identity siedzi tutaj, a nie
/// w osobnym kontekście, żeby była jedna historia migracji i jeden backup.
/// </summary>
public class PrawoRagDbContext(DbContextOptions<PrawoRagDbContext> options)
    : IdentityDbContext<AppUserEntity>(options)
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

    /// <summary>Zużycie planu i capów pojemności (E1/T-10) — trwałe, przeżywa restart.</summary>
    public DbSet<UsageCounterEntity> UsageCounters => Set<UsageCounterEntity>();

    /// <summary>Przetworzone zdarzenia webhooków płatności (E3) — idempotencja.</summary>
    public DbSet<ProcessedWebhookEntity> ProcessedWebhooks => Set<ProcessedWebhookEntity>();

    // --- analiza dokumentów (raport BEZ treści dokumentu — patrz AnalysisEntity) ---
    public DbSet<AnalysisEntity> Analyses => Set<AnalysisEntity>();
    public DbSet<AnalysisUnitEntity> AnalysisUnits => Set<AnalysisUnitEntity>();
    public DbSet<AnalysisUnitFeedbackEntity> AnalysisUnitFeedbacks => Set<AnalysisUnitFeedbackEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // KONIECZNE: buduje tabele Identity (AspNetUsers i spółka). Bez tego wywołania konta nie
        // istnieją w modelu, a migracja wychodzi pusta.
        base.OnModelCreating(b);

        b.HasPostgresExtension("vector");

        b.Entity<AppUserEntity>(e =>
        {
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.TermsVersion).HasMaxLength(40);
            e.Property(x => x.PlanId).HasMaxLength(40);
            e.Property(x => x.PlanStatus).HasMaxLength(30);
        });

        b.Entity<ProcessedWebhookEntity>(e =>
        {
            e.ToTable("processed_webhooks");
            // Klucz na identyfikatorze zdarzenia = cała idempotencja. Duplikat rozbija się o klucz,
            // więc nie ma okna, w którym dwa równoległe dostarczenia zrobiłyby robotę dwa razy.
            e.HasKey(x => x.EventId);
            e.Property(x => x.EventId).HasMaxLength(80);
            e.Property(x => x.EventType).HasMaxLength(120);
        });

        b.Entity<UsageCounterEntity>(e =>
        {
            e.ToTable("usage_counters");
            // Klucz złożony = mechanizm zerowania: nowy okres to nowy wiersz, bez zadania w tle.
            // On nadaje też indeks unikalny, na którym stoi atomowy upsert (ON CONFLICT) w CostGuard.
            e.HasKey(x => new { x.Scope, x.Key, x.PeriodStart });
            e.Property(x => x.Scope).HasMaxLength(40);
            e.Property(x => x.Key).HasMaxLength(450); // mieści identyfikator konta z Identity
        });

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

            // P6/AKT-2: TemporalAugmenter.BuildUnabsorbedDatesAsync filtruje DOKŁADNIE tym predykatem
            // (DocType='act' + EF.Functions.JsonExists, czyli operator jsonb `?`) PRZY KAŻDEJ turze
            // czatu, która zwróciła choć jeden chunk aktu. Zmierzone 2026-08-24
            // (docs/PLAN-SIZING-DEPLOY-2026-08-24.md, "Odkrycie 2"): bez indeksu to sekwencyjny skan
            // 533k wierszy (TypedMetadata to duże, czasem TOASTowane jsonb) — 700ms solo, ale 18-22s
            // pod 16 równoległymi zapytaniami czatu. To NIE jest problem mocy CPU/RAM (żaden większy
            // serwer tego nie naprawi) — to brak indeksu pod predykat. Indeks częściowy, zawężony
            // dokładnie do tego predykatu (ten sam operator `?`, żeby planner rozpoznał implikację):
            // dziś to garstka aktów z niewchłoniętymi nowelami, więc zapytanie staje się Index Scan
            // po kilku wierszach zamiast Seq Scan po całej tabeli. Kolumna nośna (`Id`) jest
            // nieistotna dla wyniku — cały filtr siedzi w `HasFilter`, standardowy idiom Postgresa dla
            // indeksu "czy w ogóle istnieje pasujący wiersz".
            e.HasIndex(x => x.Id)
                .HasDatabaseName("IX_documents_UnabsorbedAmendments")
                .HasFilter("\"DocType\" = 'act' AND \"TypedMetadata\" IS NOT NULL AND \"TypedMetadata\" ? 'unabsorbedAmendments'");
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
            // GIN dla BM25.
            e.HasIndex(x => x.SearchVector).HasMethod("gin");
            // UWAGA: indeksu HNSW dla toru gęstego NIE MA w modelu EF — jest WYRAŻENIOWY
            // (`("Embedding"::halfvec(1024)) halfvec_cosine_ops`), a `HasIndex` nie umie wyrazić rzutu
            // typu. Zarządza nim migracja SyncHalfvecEmbeddingIndex (surowy SQL). Deklarowanie tu
            // wariantu fp32 `("Embedding" vector_cosine_ops)` było AKTYWNIE SZKODLIWE: zapytanie toru
            // gęstego rzutuje obie strony `<=>` na halfvec (HybridRetriever.DenseAsync), a indeks fp32
            // takiego wyrażenia NIE OBSŁUGUJE — zmierzone planem zapytania przy `enable_seqscan=off`:
            // fp32 → `Sort + Seq Scan`, wyrażeniowy halfvec → `Index Scan`. Każde środowisko postawione
            // z samych migracji dostawało więc sequential scan po całej tabeli chunks (7,4 mln wierszy)
            // przy KAŻDYM pytaniu, bez żadnego sygnału błędu.
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
