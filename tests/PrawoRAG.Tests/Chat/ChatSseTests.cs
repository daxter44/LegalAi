using System.Text.Json;
using PrawoRAG.Api.Services;
using PrawoRAG.Llm.Grounding;

namespace PrawoRAG.Tests.Chat;

/// <summary>
/// Kontrakt SSE endpointu /api/chat (audyt OWASP LLM 2026-09-01, W1). Endpoint jest teraz cienką
/// translacją strumienia IChatService — te testy pilnują, żeby (1) KAŻDY typ zdarzenia miał ramkę
/// (nowy ChatEvent bez mapowania = odmowa bramki, która nigdy nie dotarłaby do klienta API),
/// (2) zdarzenia korekcyjne były rozpoznawalne, (3) ładunek `done` niósł wynik kontroli cytatów.
/// </summary>
public class ChatSseTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly ChatEvent[] AllEvents =
    [
        new StageEvent("dense", "Szukam…", 3),
        new NoRetrievalEvent("pogawędka"),
        new RetryingRetrievalEvent("nowe zapytanie", "brak pokrycia"),
        new SourcesEvent([new ChatSource(1, "art. 1 k.c.", "Kodeks cywilny", null, "…")]),
        new DocSourcesEvent("umowa.pdf", [new DocSource(1, "§ 1")]),
        new ProvenanceEvent(true, "model", "OmniaSI/1.0", DateTimeOffset.UnixEpoch, true),
        new TokenEvent("tekst"),
        new ReasoningDeltaEvent("myśl"),
        new RegeneratingEvent("KOREKTA: art. 999 nie istnieje w źródłach"),
        new ReasoningEvent("całe rozumowanie"),
        new AbstainEvent("Nie mam źródeł", 0.42),
        new DoneEvent(false, "model", new CitationCheck([], [], [])),
        new ChatErrorEvent("awaria"),
    ];

    [Fact]
    public void Every_chat_event_type_has_an_sse_frame()
    {
        // Wszystkie konkretne typy ChatEvent z assembly Api — nie tylko te, które ktoś pamiętał
        // dopisać do listy wyżej. Nowy typ bez wpisu w AllEvents wywala ten test, a bez mapowania
        // w ChatSse.Map wywala NotSupportedException.
        var declared = typeof(ChatEvent).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(ChatEvent)) && !t.IsAbstract)
            .OrderBy(t => t.Name).ToArray();
        var covered = AllEvents.Select(e => e.GetType()).Distinct().OrderBy(t => t.Name).ToArray();
        Assert.Equal(declared, covered);

        foreach (var evt in AllEvents)
        {
            var frame = ChatSse.Map(evt, showUsage: false);
            Assert.False(string.IsNullOrWhiteSpace(frame.Event));
            Assert.NotNull(frame.Data);
            JsonSerializer.Serialize(frame.Data, Json); // ładunek musi być serializowalny
        }
    }

    [Fact]
    public void Corrective_events_reset_the_answer_and_nothing_else_does()
    {
        var resets = AllEvents.Where(ChatSse.ResetsAnswer).Select(e => e.GetType()).ToHashSet();
        Assert.Equal([typeof(RegeneratingEvent), typeof(RetryingRetrievalEvent)], resets.OrderBy(t => t.Name));
    }

    [Fact]
    public void Gate_refusal_maps_to_abstain_after_tokens()
    {
        // Sekwencja z AnswerGate.Refuse w ChatService: tokeny → reasoning → abstain → done(abstained).
        var frames = new ChatEvent[]
        {
            new TokenEvent("Zgodnie z art. 999 …"),
            new ReasoningEvent("…"),
            new AbstainEvent("Odpowiedź odrzucona", 0.9),
            new DoneEvent(true, "model", new CitationCheck([], [], ["art. 999"])),
        }.Select(e => ChatSse.Map(e, showUsage: false)).ToList();

        Assert.Equal(["token", "reasoning", "abstain", "done"], frames.Select(f => f.Event));
        var done = JsonSerializer.SerializeToElement(frames[3].Data, Json);
        Assert.True(done.GetProperty("abstained").GetBoolean());
    }

    [Fact]
    public void Done_carries_citation_check_and_usage_only_behind_flag()
    {
        var check = new CitationCheck([], [9], ["art. 5"]);
        var done = new DoneEvent(false, "m", check, new PrawoRAG.Domain.Llm.LlmUsage(10, 20, false), Regenerated: true);

        var hidden = JsonSerializer.SerializeToElement(ChatSse.Map(done, showUsage: false).Data, Json);
        Assert.False(hidden.GetProperty("citationCheck").GetProperty("isClean").GetBoolean());
        Assert.True(hidden.GetProperty("regenerated").GetBoolean());
        Assert.False(hidden.TryGetProperty("usage", out _));

        var shown = JsonSerializer.SerializeToElement(ChatSse.Map(done, showUsage: true).Data, Json);
        Assert.Equal(20, shown.GetProperty("usage").GetProperty("outputTokens").GetInt32());
    }
}
