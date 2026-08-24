namespace PrawoRAG.Domain.Retrieval;

/// <summary>
/// Bezpiecznik JEDNOKIERUNKOWY przed routerem intencji (Zadanie 6 planu ROU): czy wiadomość zawiera
/// jawne odwołanie prawne — artykuł, paragraf, sygnaturę akt, numer Dziennika Ustaw, nazwę albo
/// akronim aktu.
///
/// Kontrakt jest asymetryczny i to jest cała jego wartość: <c>true</c> WYMUSZA retrieval niezależnie
/// od orzeczenia routera (i nawet gdy router padł), a <c>false</c> NIE znaczy „nie trzeba szukać" —
/// znaczy tylko „ten mechanizm nie ma zdania, decyduje router". Dzięki temu detektora nie da się
/// przeciążyć nowymi intencjami: on nie zgaduje intencji, on zauważa cytat.
///
/// Dlaczego to jest potrzebne obok routera: pomyłki nie kosztują tyle samo. Small-talk wpuszczony
/// do retrievalu to ~85 s straconego czasu; pytanie prawne uznane za small-talk to odpowiedź BEZ
/// źródeł, czyli złamany rdzeń produktu. Ten detektor odbiera routerowi możliwość popełnienia
/// drugiego błędu w najbardziej oczywistych przypadkach — tych, gdzie użytkownik podał adres przepisu.
///
/// ZERO nowych list słów kluczowych — w całości na istniejących, przetestowanych parserach
/// (<see cref="CitationParser"/>, <see cref="CaseNumberKey"/>, <see cref="ActEliKey"/>,
/// <see cref="AcronymDetector"/>), tych samych, które zasilają tory DOKŁADNE retrievalu. Nowy wzorzec
/// rozpoznawany przez tory jest więc automatycznie rozpoznawany tutaj.
/// </summary>
public static class LegalTokenDetector
{
    /// <summary>
    /// <c>true</c> ⇒ retrieval wymuszony. Czysta funkcja, bez I/O — leci na wejściu każdej tury,
    /// przed jakimkolwiek wywołaniem modelu (także przed routerem, żeby oszczędzić to wywołanie).
    /// </summary>
    public static bool ContainsLegalReference(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        // Artykuł (+ paragraf) — ten sam parser, co tor strukturalny.
        if (CitationParser.Parse(text).Count > 0) return true;

        // Sama nazwa/skrót aktu, BEZ artykułu („ustawa o ochronie danych osobowych", „ordynacja
        // podatkowa", „KC"). Parse wymaga artykułu, więc te przypadki trzeba wziąć osobno —
        // wykryte testem, nie założone.
        if (CitationParser.ExtractActHint(text) is not null) return true;

        // Goły paragraf („a co z § 2?") — typowy follow-up bez numeru artykułu w tej turze.
        if (text.Contains('§')) return true;

        // Sygnatura akt („III SA/Po 154/26") — tor sygnaturowy.
        if (CaseNumberKey.Detect(text).Count > 0) return true;

        // Numer Dziennika Ustaw / ELI („Dz.U. 2025 poz. 1815", „DU/2011/657") — tor odwołania do aktu.
        if (ActEliKey.Detect(text).Count > 0) return true;

        // Akronim aktu (np. „KSeF") — tor akronimowy. Detektor akronimów jest heurystyczny i bywa
        // fałszywie pozytywny na zwykłym słowie pisanym wielkimi literami; tutaj to NIEGROŹNE:
        // fałszywe true kosztuje wyłącznie zbędny retrieval, czyli dzisiejsze zachowanie systemu.
        return AcronymDetector.Extract(text).Count > 0;
    }
}
