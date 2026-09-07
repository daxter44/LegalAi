using PrawoRAG.Llm;

namespace PrawoRAG.Tests.Llm;

/// <summary>
/// T-REASON — automat rozdzielający „rozumowanie" (thinking) od widocznej treści. Pokrywa oba realne
/// warianty: Google AI Studio (flaga google.thought + literalne tagi &lt;thought&gt; jako artefakt) i
/// self-hosted (gołe tagi &lt;think&gt;), w tym tag rozcięty między deltami; oraz brak rozumowania
/// (Claude/Bielik) = czysty pass-through bez regresji.
/// </summary>
public class ReasoningSplitterTests
{
    /// <summary>Skleja widoczne fragmenty ze wszystkich delt + Finish (jak provider).</summary>
    private static (string Visible, string Reasoning, bool Has) Run(IEnumerable<(string Content, bool Thought)> deltas)
    {
        var s = new ReasoningSplitter();
        var vis = "";
        foreach (var (c, t) in deltas) vis += s.Push(c, t);
        vis += s.Finish();
        return (vis, s.Reasoning, s.HasReasoning);
    }

    [Fact] // Google: delty myślenia z flagą (open <thought> jako artefakt), delta zamykająca bez flagi zaczyna od </thought>
    public void Google_flagged_thought_then_unflagged_visible()
    {
        var (vis, reasoning, has) = Run(
        [
            ("<thought>Rozważam, czy niebo", true),
            (" jest niebieskie.", true),
            ("</thought>Tak, niebo jest niebieskie [1].", false),
        ]);

        Assert.Equal("Tak, niebo jest niebieskie [1].", vis);
        Assert.True(has);
        Assert.Equal("Rozważam, czy niebo jest niebieskie.", reasoning);
        Assert.DoesNotContain("<thought>", vis + reasoning); // tagi odrzucone z OBU
        Assert.DoesNotContain("</thought>", vis + reasoning);
    }

    [Fact] // self-hosted: gołe <think>…</think> w treści, bez żadnej flagi
    public void Selfhosted_think_tags_no_flag()
    {
        var (vis, reasoning, has) = Run(
        [
            ("<think>Krok 1. Krok 2.</think>", false),
            ("Odpowiedź końcowa.", false),
        ]);

        Assert.Equal("Odpowiedź końcowa.", vis);
        Assert.Equal("Krok 1. Krok 2.", reasoning);
        Assert.True(has);
    }

    [Fact] // tag rozcięty na granicy delt nie może przeciekać do treści
    public void Tag_split_across_deltas()
    {
        var (vis, reasoning, _) = Run(
        [
            ("<thi", false), ("nk>myśl</thi", false), ("nk>widoczne", false),
        ]);

        Assert.Equal("widoczne", vis);
        Assert.Equal("myśl", reasoning);
    }

    [Fact] // brak rozumowania (Claude/Bielik) → pass-through, zero regresji
    public void No_reasoning_is_passthrough()
    {
        var (vis, reasoning, has) = Run([("Zwykła ", false), ("odpowiedź [1].", false)]);

        Assert.Equal("Zwykła odpowiedź [1].", vis);
        Assert.False(has);
        Assert.Equal("", reasoning);
    }

    [Fact] // pierwsza linia widocznej odpowiedzi = WERDYKT (regres z /analiza): thinking nie może jej poprzedzić
    public void Verdict_first_line_is_clean_after_stripping()
    {
        var (vis, _, _) = Run(
        [
            ("<thought>Analiza fragmentu §7…", true),
            ("</thought>WERDYKT: RYZYKO\nKlauzula abuzywna [1].", false),
        ]);

        Assert.StartsWith("WERDYKT: RYZYKO", vis);
    }

    [Fact] // sama treść myślenia, brak widocznej (np. urwane) → visible puste, reasoning zebrane
    public void Only_reasoning_no_visible()
    {
        var (vis, reasoning, has) = Run([("<think>tylko myśl</think>", false)]);
        Assert.Equal("", vis);
        Assert.Equal("tylko myśl", reasoning);
        Assert.True(has);
    }

    [Fact] // flaga google bez literalnych tagów (gdyby model ich nie wpletł) — treść i tak trafia do rozumowania
    public void Google_flag_without_literal_tags()
    {
        var (vis, reasoning, _) = Run(
        [
            ("myślę bez tagów", true),
            ("widoczna odpowiedź", false),
        ]);

        Assert.Equal("widoczna odpowiedź", vis);
        Assert.Equal("myślę bez tagów", reasoning);
    }

    // --- Zadanie 1: delty rozumowania NA ŻYWO (callback), a nie tylko całość na końcu ---
    // Powód: 41 z 85 s odpowiedzi to rozumowanie, które fizycznie leci po drucie, a użytkownik
    // dostawał je dopiero po zakończeniu strumienia (ChatService emitował ReasoningEvent po pętli).

    /// <summary>Jak <see cref="Run"/>, ale zbiera też delty rozumowania przez callback splittera.</summary>
    private static (string Visible, string Reasoning, List<string> Deltas) RunWithDeltas(
        IEnumerable<(string Content, bool Thought)> deltas)
    {
        var captured = new List<string>();
        var s = new ReasoningSplitter(captured.Add);
        var vis = "";
        foreach (var (c, t) in deltas) vis += s.Push(c, t);
        vis += s.Finish();
        return (vis, s.Reasoning, captured);
    }

    [Fact] // Rownowaznosc: konkatenacja delt == Reasoning. Dowod, ze zapis do historii sie NIE zmienia.
    public void Reasoning_deltas_concatenate_to_full_reasoning_google_mode()
    {
        var (vis, reasoning, deltas) = RunWithDeltas(
        [
            ("<thought>Rozważam, czy niebo", true),
            (" jest niebieskie.", true),
            ("</thought>Tak, niebo jest niebieskie [1].", false),
        ]);

        Assert.Equal("Tak, niebo jest niebieskie [1].", vis);
        Assert.Equal(reasoning, string.Concat(deltas));
        Assert.NotEmpty(deltas);
    }

    [Fact] // Tryb self-hosted (gole <think>) MUSI tez dawac delty — inaczej Bielik/Ollama nie pokaza myslenia.
    public void Reasoning_deltas_work_for_bare_think_tags()
    {
        var (vis, reasoning, deltas) = RunWithDeltas(
        [
            ("<think>krok pierwszy", false),
            (" krok drugi</think>Odpowiedź [1].", false),
        ]);

        Assert.Equal("Odpowiedź [1].", vis);
        Assert.Equal(reasoning, string.Concat(deltas));
        Assert.Equal("krok pierwszy krok drugi", reasoning);
    }

    [Fact] // Delty WIDOCZNE nie moga wyciekac do callbacku rozumowania (i odwrotnie).
    public void Visible_deltas_never_reach_reasoning_callback()
    {
        var (vis, _, deltas) = RunWithDeltas([("czysta odpowiedź bez myślenia", false)]);

        Assert.Equal("czysta odpowiedź bez myślenia", vis);
        Assert.Empty(deltas);
    }

    [Fact] // Tagi sa delimiterami — nie moga trafic do delt (callback dostaje tresc, nie artefakty).
    public void Reasoning_deltas_never_contain_tags()
    {
        var (_, _, deltas) = RunWithDeltas(
        [
            ("<thought>myśl", true),
            ("</thought>widoczne", false),
        ]);

        Assert.DoesNotContain("<thought>", string.Concat(deltas));
        Assert.DoesNotContain("</thought>", string.Concat(deltas));
    }

    [Fact] // Brak callbacku (null) = zachowanie identyczne jak dotad — zero kosztu, zero regresji.
    public void Null_callback_keeps_today_behaviour()
    {
        var withCb = RunWithDeltas([("<think>m</think>widoczne", false)]);
        var without = Run([("<think>m</think>widoczne", false)]);

        Assert.Equal(without.Visible, withCb.Visible);
        Assert.Equal(without.Reasoning, withCb.Reasoning);
    }
}
