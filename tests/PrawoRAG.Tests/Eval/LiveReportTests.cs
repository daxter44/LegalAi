using Microsoft.EntityFrameworkCore;
using PrawoRAG.Api.Services;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Llm.Grounding;
using PrawoRAG.Storage;
using PrawoRAG.Storage.Entities;

namespace PrawoRAG.Tests.Eval;

/// <summary>
/// T-LIVEREPORT (Zadanie 16 planu ROU) — metryka odmów liczona na historii tabeli <c>messages</c>.
///
/// Testy pilnują logiki ZLICZANIA, bo od niej zależy, czy metryka nadrzędna mówi prawdę. Najważniejsze
/// rozróżnienie: odmowa PROGOWA (kolumna <c>Abstained</c>) vs TREŚCIOWA (model dostał źródła i sam
/// orzekł, że nie odpowiadają — fraza w treści przy <c>Abstained = false</c>). Bez ich rozdzielenia
/// raport pokazywałby połowę prawdy, a to właśnie druga jest u nas ważniejsza.
///
/// Sam runner pisze na konsolę, więc weryfikujemy zapytania SQL/LINQ na tych samych danych, jakie
/// on czyta — na żywym Postgresie, tak jak reszta testów storage.
/// </summary>
[Collection("LiveDb")]
public class LiveReportTests
{
    private static readonly string Conn =
        Environment.GetEnvironmentVariable("PRAWORAG_DB")
        ?? "Host=localhost;Port=5432;Database=praworag;Username=praworag;Password=praworag";

    private static PrawoRagDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PrawoRagDbContext>().UseNpgsql(Conn, o => o.UseVector()).Options);

    private const string UserId = "test-live-report";

    private static async Task CleanAsync()
    {
        await using var db = NewDb();
        await db.Conversations.Where(c => c.UserId == UserId).ExecuteDeleteAsync();
    }

    /// <summary>Zasiewa rozmowę: pary (pytanie użytkownika, odpowiedź asystenta).</summary>
    private static async Task SeedAsync(
        params (string Question, string Answer, bool Abstained, bool? Clean, string? Route, bool Regenerated)[] turns)
    {
        await using var db = NewDb();
        var now = DateTimeOffset.UtcNow;
        var conv = new ConversationEntity
        {
            Id = Guid.CreateVersion7(), UserId = UserId, Title = "test",
            CreatedAt = now, UpdatedAt = now,
        };
        db.Conversations.Add(conv);

        var i = 0;
        foreach (var t in turns)
        {
            db.Messages.Add(new MessageEntity
            {
                Id = Guid.CreateVersion7(), ConversationId = conv.Id, Role = "user",
                Content = t.Question, CreatedAt = now.AddSeconds(i++),
            });
            db.Messages.Add(new MessageEntity
            {
                Id = Guid.CreateVersion7(), ConversationId = conv.Id, Role = "assistant",
                Content = t.Answer, CreatedAt = now.AddSeconds(i++),
                Abstained = t.Abstained, CitationClean = t.Clean, Route = t.Route, Regenerated = t.Regenerated,
            });
        }
        await db.SaveChangesAsync();
    }

    private sealed record AssistantRow(
        bool Abstained, bool? Clean, string? Route, bool Regenerated, string Content);

    /// <summary>
    /// Rekord, NIE krotka: projekcja LINQ na <c>ValueTuple</c> tłumaczy się na typ <c>record</c>
    /// Postgresa, którego Npgsql domyślnie nie potrafi odczytać (wymaga <c>EnableRecordsAsTuples</c>).
    /// Nazwany typ omija problem i czyta się lepiej w asercjach.
    /// </summary>
    private static async Task<List<AssistantRow>> AssistantRowsAsync()
    {
        await using var db = NewDb();
        return await db.Messages
            .Where(m => m.Role == "assistant" && m.Conversation!.UserId == UserId)
            .Select(m => new AssistantRow(m.Abstained, m.CitationClean, m.Route, m.Regenerated, m.Content))
            .ToListAsync();
    }

    [Fact] // Odmowa TRESCIOWA liczona OSOBNO od progowej - to rozroznienie jest sercem metryki.
           // REALISTYCZNE dane: model pisze TYLKO krotka fraze z reguly 3 (bez doklejki "Zawez
           // pytanie..." z AbstentionPolicy.Message) - stary test zasiewal pelny Message i przez to
           // nie wykryl, ze produkcyjne odmowy tresciowe nie matchuja (fix 2026-08-31). Do tego
           // wiersz STAREJ epoki (odmowa tresciowa z Abstained=false, sprzed wariantu A) - raport
           // musi liczyc obie epoki tak samo.
    public async Task Content_refusal_counted_separately_from_threshold()
    {
        const string modelRefusal = "Nie mam wystarczających źródeł, aby odpowiedzieć."; // fraza reguły 3, bez doklejki
        await CleanAsync();
        await SeedAsync(
            ("pytanie 1", AbstentionPolicy.Message, true, null, ChatRoutes.Retrieval, false),  // progowa (bramka)
            ("pytanie 2", modelRefusal, true, true, ChatRoutes.Retrieval, false),              // treściowa, nowa epoka
            ("pytanie 3", modelRefusal, false, true, ChatRoutes.Retrieval, false),             // treściowa, STARA epoka
            ("pytanie 4", "Odpowiadasz z art. 415 [1].", false, true, ChatRoutes.Retrieval, false));

        var rows = await AssistantRowsAsync();

        // Predykaty identyczne jak w LiveReportRunner (IsContentRefusal / IsAnyRefusal).
        bool IsContent(AssistantRow r) =>
            r.Content.Contains(GroundedPrompt.RefusalMarker, StringComparison.OrdinalIgnoreCase)
            && !r.Content.Contains(AbstentionPolicy.Message, StringComparison.Ordinal);
        bool IsAny(AssistantRow r) =>
            r.Abstained || r.Content.Contains(GroundedPrompt.RefusalMarker, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(3, rows.Count(IsAny));                     // metryka nadrzędna: wszystkie odmowy
        Assert.Equal(2, rows.Count(IsContent));                 // treściowe: obie epoki
        Assert.Equal(1, rows.Count(r => IsAny(r) && !IsContent(r))); // progowa
        Assert.Equal(4, rows.Count);
        await CleanAsync();
    }

    [Fact] // Wiadomosci UZYTKOWNIKA nie wchodza do mianownika - inaczej kazda metryka bylaby
           // rozcienczona o polowe i nie do porownania miedzy biegami.
    public async Task User_messages_are_excluded_from_denominator()
    {
        await CleanAsync();
        await SeedAsync(
            ("pytanie 1", "odpowiedź 1", false, true, ChatRoutes.Retrieval, false),
            ("pytanie 2", "odpowiedź 2", false, true, ChatRoutes.Retrieval, false));

        await using var db = NewDb();
        var all = await db.Messages.CountAsync(m => m.Conversation!.UserId == UserId);
        var assistant = await db.Messages.CountAsync(m => m.Role == "assistant" && m.Conversation!.UserId == UserId);

        Assert.Equal(4, all);
        Assert.Equal(2, assistant);
        await CleanAsync();
    }

    [Fact] // Rozklad Route: bez tego pola trafnosc routera bylaby niemierzalna, a jego wlaczenie
           // na produkcji byloby wiara, nie decyzja.
    public async Task Route_distribution_is_countable()
    {
        await CleanAsync();
        await SeedAsync(
            ("siema", "Cześć!", false, null, ChatRoutes.Smalltalk, false),
            ("art. 415?", "Odpowiedź [1].", false, true, ChatRoutes.Retrieval, false),
            ("stare pytanie", "stara odpowiedź", false, true, null, false)); // przed routerem

        var rows = await AssistantRowsAsync();

        Assert.Equal(1, rows.Count(r => r.Route == ChatRoutes.Smalltalk));
        Assert.Equal(1, rows.Count(r => r.Route == ChatRoutes.Retrieval));
        Assert.Equal(1, rows.Count(r => r.Route is null));
        await CleanAsync();
    }

    [Fact] // Regeneracje bramki policzalne - to material do pomiaru falszywych alarmow (prog: >10%).
    public async Task Regenerations_are_countable()
    {
        await CleanAsync();
        await SeedAsync(
            ("pytanie 1", "Odpowiedź [1].", false, true, ChatRoutes.Retrieval, true),
            ("pytanie 2", "Odpowiedź [1].", false, true, ChatRoutes.Retrieval, false));

        var rows = await AssistantRowsAsync();

        Assert.Equal(1, rows.Count(r => r.Regenerated));
        await CleanAsync();
    }

    [Fact] // Pytania zakonczone odmowa dajace sie odtworzyc z par (user -> assistant) - bez tego
           // raport mowi ILE odmow, ale nie NA CO.
    public async Task Refused_questions_are_recoverable_from_pairs()
    {
        await CleanAsync();
        await SeedAsync(
            ("komu zgłosić wyciek danych?", AbstentionPolicy.Message, true, null, ChatRoutes.Retrieval, false),
            ("co z art. 415?", "Odpowiadasz [1].", false, true, ChatRoutes.Retrieval, false));

        await using var db = NewDb();
        var ordered = await db.Messages
            .Where(m => m.Conversation!.UserId == UserId)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.Role, m.Content, m.Abstained })
            .ToListAsync();

        var refused = new List<string>();
        for (var i = 1; i < ordered.Count; i++)
            if (ordered[i].Role == "assistant" && ordered[i - 1].Role == "user" &&
                (ordered[i].Abstained || ordered[i].Content.Contains(AbstentionPolicy.Message)))
                refused.Add(ordered[i - 1].Content);

        Assert.Equal("komu zgłosić wyciek danych?", Assert.Single(refused));
        await CleanAsync();
    }

    [Fact] // Pusta baza nie moze rzucac - raport ma powiedziec "nic do policzenia".
    public async Task Empty_set_produces_no_rows()
    {
        await CleanAsync();
        var rows = await AssistantRowsAsync();
        Assert.Empty(rows);
    }
}
