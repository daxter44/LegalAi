using PrawoRAG.Domain.Llm;
using PrawoRAG.Domain.Retrieval;

namespace PrawoRAG.Tests.Retrieval;

/// <summary>
/// Heurystyka follow-upów (czysta — bez DB/LLM): sklejenie poprzednich pytań użytkownika z dopytaniem
/// buduje wariant zapytania z kontekstem. Kolejność chronologiczna, cap na 2 ostatnie poprzednie pytania.
/// </summary>
public class FollowUpQueryTests
{
    [Fact]
    public void Empty_history_returns_question_unchanged()
        => Assert.Equal("a co z § 2?", FollowUpQuery.Contextualize(Array.Empty<string>(), "a co z § 2?"));

    [Fact]
    public void Joins_previous_questions_chronologically_before_current()
        => Assert.Equal(
            "co mówi art. 367 KPC? a co z § 2?",
            FollowUpQuery.Contextualize(["co mówi art. 367 KPC?"], "a co z § 2?"));

    [Fact]
    public void Caps_at_two_most_recent_previous_questions()
        => Assert.Equal(
            "pytanie B pytanie C dopytanie",
            FollowUpQuery.Contextualize(["pytanie A", "pytanie B", "pytanie C"], "dopytanie"));

    [Fact]
    public void Skips_blank_previous_questions()
        => Assert.Equal(
            "pytanie A dopytanie",
            FollowUpQuery.Contextualize(["pytanie A", "  ", ""], "dopytanie"));

    // --- PickContextual: wybór ASYMETRYCZNY z marginesem (fałszywe surowe >> fałszywe kontekstowe) ---

    [Fact]
    public void Noise_level_difference_picks_contextual()
    {
        // Regresja z M4: surowe „a co z § 2?" 0.879001 do PRZYPADKOWYCH fragmentów vs kontekstowe
        // 0.879000 do właściwego artykułu — różnica 1e-6 to szum, nie sygnał. Ostre `>` wybierało
        // gorszy surowy wariant.
        Assert.True(FollowUpQuery.PickContextual(rawSignal: 0.879008, contextualSignal: 0.879000));
    }

    [Fact]
    public void Raw_clearly_stronger_beats_margin_and_wins()
        => Assert.False(FollowUpQuery.PickContextual(rawSignal: 0.85, contextualSignal: 0.60));

    [Fact]
    public void Raw_within_margin_still_loses()
        // Surowe wyżej, ale o mniej niż margines → kontekstowe (asymetria kosztów pomyłek).
        => Assert.True(FollowUpQuery.PickContextual(
            rawSignal: 0.879, contextualSignal: 0.879 - FollowUpQuery.DefaultSignalMargin + 0.001));

    [Fact]
    public void Custom_margin_is_respected()
    {
        Assert.False(FollowUpQuery.PickContextual(0.70, 0.65, margin: 0.02)); // 0.05 > 0.02 → surowe
        Assert.True(FollowUpQuery.PickContextual(0.70, 0.65, margin: 0.10));  // 0.05 ≤ 0.10 → kontekstowe
    }

    // --- Overload ChatTurn: fold kotwic ostatniej realnej odpowiedzi (cytat + rzeczowniki + źródła) ---

    private const string LasAnswer =
        "Grzywna może wynikać z art. 157 § 1 kodeksu wykroczeń, jeżeli osoba nie opuści lasu na żądanie osoby uprawnionej.";

    [Fact]
    public void Folds_last_answer_citation_and_nouns_into_query()
    {
        // Zgłoszony przypadek: most („art. 157", „las") był tylko w odpowiedzi asystenta, nie w pytaniach.
        var history = new[] { new ChatTurn("jakie są konsekwencje nocowania w lesie?", LasAnswer) };
        var result = FollowUpQuery.Contextualize(history, "a kim jest osoba uprawniona z powyższej odpowiedzi?");

        Assert.Contains("a kim jest osoba uprawniona", result); // bieżące pytanie obecne
        Assert.Contains("art. 157", result);                    // cytat wyłuskany (tor strukturalny)
        Assert.Contains("las", result);                         // rzeczownik z odpowiedzi (dense/BM25)
    }

    [Fact]
    public void Skips_refusal_turn_and_reaches_last_nonnull_answer()
    {
        // Q1 z realną odpowiedzią, Q2 = odmowa (Answer=null) → fold sięga wstecz do Q1 (zgłoszone Q2→Q3).
        var history = new[]
        {
            new ChatTurn("jakie są konsekwencje nocowania w lesie?", LasAnswer),
            new ChatTurn("a kim jest osoba uprawniona?", null),
        };
        var result = FollowUpQuery.Contextualize(history, "a kim jest osoba uprawniona z powyższej odpowiedzi?");

        Assert.Contains("art. 157", result); // kotwica z Q1 mimo odmowy w Q2
    }

    [Fact]
    public void All_answers_null_falls_back_to_question_only_context()
    {
        var history = new[] { new ChatTurn("pierwsze pytanie", null) };
        Assert.Equal("pierwsze pytanie drugie", FollowUpQuery.Contextualize(history, "drugie"));
    }

    [Fact]
    public void Empty_history_overload_returns_question_unchanged()
        => Assert.Equal("dopytanie", FollowUpQuery.Contextualize(Array.Empty<ChatTurn>(), "dopytanie"));

    [Fact]
    public void Strips_citation_markers_from_folded_snippet()
    {
        var history = new[] { new ChatTurn("q", "Zgodnie z art. 157 [1] chodzi o las [2].") };
        var result = FollowUpQuery.Contextualize(history, "dopytanie");

        Assert.DoesNotContain("[1]", result);
        Assert.DoesNotContain("[2]", result);
    }

    [Fact]
    public void Citation_survives_even_when_snippet_is_truncated_before_it()
    {
        // Cytat leży ZA budżetem fragmentu, ale jest wyłuskiwany z całej odpowiedzi i doklejany osobno.
        var answer = new string('x', FollowUpQuery.MaxFoldedAnswerChars + 100) + " art. 157 kodeksu wykroczeń";
        var history = new[] { new ChatTurn("q", answer) };
        var result = FollowUpQuery.Contextualize(history, "dopytanie");

        Assert.Contains("art. 157", result); // cytat przetrwał
        Assert.Contains("…", result);          // fragment przycięty do budżetu
    }

    [Fact]
    public void Folds_source_anchors_when_present()
    {
        var history = new[]
        {
            new ChatTurn("q", "krótka odpowiedź", new[] { "art. 157 § 1 KW", "Kodeks wykroczeń" }),
        };
        var result = FollowUpQuery.Contextualize(history, "dopytanie");

        Assert.Contains("Kodeks wykroczeń", result);
        Assert.Contains("art. 157 § 1 KW", result);
    }

    [Fact]
    public void Question_context_leads_before_folded_answer()
    {
        var history = new[] { new ChatTurn("pierwsze", "odpowiedź z art. 5 KC") };
        var result = FollowUpQuery.Contextualize(history, "drugie");

        Assert.StartsWith("pierwsze drugie", result); // rdzeń (pytania) prowadzi, fold za nim
    }

    // --- ContextualizeForExactMatch: tekst dla torów DOKŁADNYCH = TYLKO pytania usera, bez foldu ---

    [Fact] // rdzeń bugu: kotwica wyroku z ODPOWIEDZI systemu nie może zasilać exact-match (sygnatura/DzU)
    public void ExactMatch_text_excludes_answer_anchors_and_citations()
    {
        var history = new[]
        {
            new ChatTurn(
                "jak kwalifikować obiekty do podatku od nieruchomości?",
                "Zgodnie z orzecznictwem [2] art. 1a decyduje przeznaczenie.",
                new[] { "Wojewódzki Sąd Administracyjny w Poznaniu, I SA/Po 594/17" }),
        };
        var q = "a Art. 1a USTAWA O PODATKACH I OPŁATACH LOKALNYCH ?";

        var exact = FollowUpQuery.ContextualizeForExactMatch(history, q);
        var semantic = FollowUpQuery.Contextualize(history, q);

        // Exact-match NIE widzi sygnatury wyroku (była tylko w kotwicy odpowiedzi) — bug naprawiony.
        Assert.DoesNotContain("I SA/Po 594/17", exact);
        // ...ale wariant semantyczny DALEJ ją niesie (recall pod anaforę bez zmian).
        Assert.Contains("I SA/Po 594/17", semantic);
        // Bieżące pytanie usera (z jego cytatem) jest w tekście exact-match — tor strukturalny odpali.
        Assert.Contains("Art. 1a", exact);
    }

    [Fact] // sygnatura/cytat, który user SAM wpisał w poprzednim pytaniu, ZOSTAJE (follow-up dalej działa)
    public void ExactMatch_text_keeps_signature_from_user_question()
    {
        var history = new[] { new ChatTurn("streść wyrok I SA/Po 594/17", "To orzeczenie dotyczy...") };
        var exact = FollowUpQuery.ContextualizeForExactMatch(history, "a co z kosztami?");

        Assert.Contains("I SA/Po 594/17", exact); // z PYTANIA usera, nie z odpowiedzi → zostaje
        Assert.Contains("a co z kosztami?", exact);
    }

    [Fact]
    public void ExactMatch_text_empty_history_is_question_only()
        => Assert.Equal("dopytanie", FollowUpQuery.ContextualizeForExactMatch(Array.Empty<ChatTurn>(), "dopytanie"));
}
