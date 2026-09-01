namespace PrawoRAG.Api.Services;

/// <summary>Jedna ramka SSE: nazwa zdarzenia + ładunek do serializacji JSON (camelCase).</summary>
public sealed record SseFrame(string Event, object Data);

/// <summary>
/// Mapowanie strumienia <see cref="ChatEvent"/> z <see cref="IChatService"/> na zdarzenia SSE
/// endpointu <c>/api/chat</c>. Wydzielone z Program.cs, żeby kontrakt był testowalny bez HTTP
/// i żeby /api/chat NIE MIAŁ własnej kopii pipeline'u.
///
/// Dlaczego to istnieje (audyt OWASP LLM 2026-09-01, ustalenie W1): endpoint SSE miał osobną,
/// uproszczoną implementację — bez bramki anty-fabrykacji (<c>AnswerGate</c>), bez pętli domykającej
/// i bez routera. Odpowiedź z wymyślonym artykułem wychodziła w całości, a wynik
/// <c>CitationValidator</c> był tylko flagą w <c>done</c>, którą klient mógł zignorować. Teraz oba
/// tory (Blazor i SSE) jadą tym samym <see cref="IChatService"/>, a tu jest wyłącznie translacja.
///
/// Kontrakt dla klienta SSE (kolejność jak w ChatEvents.cs):
/// <list type="bullet">
/// <item><c>stage {stage,label,count}</c> — etap pracy (informacyjny).</item>
/// <item><c>no_retrieval {reason}</c> — router pominął bazę: odpowiedź NIE jest ugruntowana w źródłach.</item>
/// <item><c>retrying_retrieval {newQuery,reason}</c> — druga runda: klient CZYŚCI zebrane tokeny i rozumowanie.</item>
/// <item><c>sources [ChatSource]</c> — źródła; mogą przyjść ponownie po <c>retrying_retrieval</c>.</item>
/// <item><c>doc_sources {fileName,fragments}</c> — fragmenty załącznika (dziś /api/chat bez załączników).</item>
/// <item><c>provenance {aiGenerated,model,system,generatedAt,grounded}</c> — oznaczenie AI Act, raz, przed pierwszym tokenem.</item>
/// <item><c>token {text}</c>, <c>reasoning_delta {text}</c> — streaming.</item>
/// <item><c>regenerating {reason}</c> — bramka zawróciła odpowiedź: klient CZYŚCI zebrane tokeny i rozumowanie.</item>
/// <item><c>reasoning {text}</c> — pełne rozumowanie (nadpisuje delty).</item>
/// <item><c>abstain {message,maxSimilarity}</c> — odmowa progowa LUB po dwóch brudnych próbach: klient
///   ZASTĘPUJE zebraną treść komunikatem (tokeny mogły już dotrzeć).</item>
/// <item><c>done {abstained,model,citationCheck,regenerated[,usage]}</c> — koniec tury.</item>
/// <item><c>error {message}</c> — błąd; może wystąpić w dowolnym momencie.</item>
/// </list>
/// </summary>
public static class ChatSse
{
    /// <summary>Zdarzenia, po których klient ma odrzucić dotąd zebraną treść odpowiedzi (a serwer —
    /// wyzerować licznik znaków do budżetu pojemności, jak <c>ex.Answer = ""</c> w Chat.razor).</summary>
    public static bool ResetsAnswer(ChatEvent evt) => evt is RegeneratingEvent or RetryingRetrievalEvent;

    /// <param name="showUsage">Diagnostics:ShowTokenUsage — tokeny in/out w <c>done</c> tylko za flagą.</param>
    public static SseFrame Map(ChatEvent evt, bool showUsage) => evt switch
    {
        StageEvent s => new("stage", new { stage = s.Stage, label = s.Label, count = s.Count }),
        NoRetrievalEvent nr => new("no_retrieval", new { reason = nr.Reason }),
        RetryingRetrievalEvent rr => new("retrying_retrieval", new { newQuery = rr.NewQuery, reason = rr.Reason }),
        SourcesEvent s => new("sources", s.Sources),
        DocSourcesEvent d => new("doc_sources", new { fileName = d.FileName, fragments = d.Fragments }),
        ProvenanceEvent p => new("provenance", new
        {
            aiGenerated = p.AiGenerated, model = p.Model, system = p.System,
            generatedAt = p.GeneratedAt, grounded = p.Grounded,
        }),
        TokenEvent t => new("token", new { text = t.Text }),
        ReasoningDeltaEvent rd => new("reasoning_delta", new { text = rd.Text }),
        RegeneratingEvent rg => new("regenerating", new { reason = rg.Reason }),
        ReasoningEvent r => new("reasoning", new { text = r.Text }),
        AbstainEvent a => new("abstain", new { message = a.Message, maxSimilarity = a.MaxSimilarity }),
        DoneEvent d => new("done", showUsage
            ? new { abstained = d.Abstained, model = d.Model, citationCheck = d.Check, regenerated = d.Regenerated, usage = d.Usage }
            : (object)new { abstained = d.Abstained, model = d.Model, citationCheck = d.Check, regenerated = d.Regenerated }),
        ChatErrorEvent err => new("error", new { message = err.Message }),
        // Nowy typ ChatEvent bez mapowania = błąd kompilacji semantyczny, który ma wybuchnąć w teście
        // (ChatSseTests sprawdza każdy typ), a nie zginąć po cichu jako pominięte zdarzenie.
        _ => throw new NotSupportedException($"Brak mapowania SSE dla {evt.GetType().Name}"),
    };
}
