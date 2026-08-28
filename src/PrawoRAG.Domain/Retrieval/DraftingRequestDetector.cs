using System.Text.RegularExpressions;

namespace PrawoRAG.Domain.Retrieval;

/// <summary>
/// Deterministyczny detektor PROŚBY O SPORZĄDZENIE DOKUMENTU („przygotuj umowę najmu", „napisz
/// wezwanie do zapłaty", „wzór pozwu") — Horyzont 0 obsługi draftingu (rozmowa 2026-08-28):
/// system NIE sporządza pism, ale zamiast niezdefiniowanego zachowania (odmowa „źródła nie
/// pozwalają" albo pseudo-dokument poszyty cytatami) ma odpowiedzieć wymogami prawnymi takiego
/// dokumentu ze źródłami. Wykrycie: (a) wymusza retrieval (wymogi trzeba znaleźć w przepisach,
/// więc router nie ma tu nic do powiedzenia), (b) dokleja do promptu <c>GroundedPrompt.DraftingRules</c>,
/// (c) jest zliczane w logu — skala takich próśb w becie to sygnał produktowy pod Horyzont 1
/// (generowanie prostych pism — zadanie po deployu MVP).
///
/// Kontrakt asymetryczny jak w <see cref="LegalTokenDetector"/>: fałszywy NEGATYW jest tani
/// (dzisiejsze zachowanie), fałszywy POZYTYW też jest łagodny (użytkownik pytający „jak napisać
/// umowę?" dostaje dokładnie to samo: wymogi ze źródłami) — mimo to detektor jest konserwatywny:
/// łapie tryb rozkazujący/prośbę + rzeczownik dokumentu, nie każde zdanie o umowie.
/// Czysta funkcja, bez I/O — jak <see cref="LegalTokenDetector"/>.
/// </summary>
public static class DraftingRequestDetector
{
    // Czasownik sporządzania w formie prośby: tryb rozkazujący („przygotuj"), pytanie o przysługę
    // („czy możesz przygotować"), deklaracja potrzeby („potrzebuję", „proszę o przygotowanie").
    private const string Verbs =
        @"(?:przygotuj(?:cie)?|napisz(?:cie)?|sporz[ąa]d[źz](?:cie)?|stw[óo]rz(?:cie)?|zr[óo]b(?:cie)?|" +
        @"wygeneruj(?:cie)?|zredaguj(?:cie)?|opracuj(?:cie)?|naszkicuj(?:cie)?|" +
        @"(?:czy\s+)?(?:mo[żz]esz|m[óo]g[łl]by[śs]|mog[łl]aby[śs])\s+(?:mi\s+)?" +
        @"(?:przygotowa[ćc]|napisa[ćc]|sporz[ąa]dzi[ćc]|stworzy[ćc]|zredagowa[ćc]|opracowa[ćc]|wygenerowa[ćc])|" +
        @"prosz[ęe]\s+o\s+(?:przygotowanie|napisanie|sporz[ąa]dzenie|stworzenie|zredagowanie|opracowanie))";

    // Rzeczownik dokumentu (mianownik/biernik/dopełniacz) — typy pism, o które realnie proszą
    // użytkownicy nieprawniczy. Lista celowo zamknięta: „dokument"/„tekst" są za szerokie.
    private const string Nouns =
        @"(?:umow[aęy]|wezwani[ea]|pozw[uy]|pozew|pism[oa]|wniosk?[ua]?|wniosek|odwo[łl]ani[ea]|" +
        @"wypowiedzeni[ea]|aneks[u]?|regulamin[u]?|o[śs]wiadczeni[ea]|ugod[aęy]|pe[łl]nomocnictw[oa]|" +
        @"skarg[aęi]|za[żz]aleni[ea]|apelacj[ięaę]|sprzeciw[u]?|statut[u]?|uchwa[łl][aęy]|" +
        @"porozumieni[ea]|testament[u]?|upowa[żz]nieni[ea]|reklamacj[ięa]|not[aęy]\s+obci[ąa][żz]eniow|" +
        @"odst[ąa]pieni[ea]|wypowiedzenie|monit[u]?)";

    // Czasownik, potem rzeczownik w oknie do 6 słów. Okno jest po to, żeby złapać „przygotuj mi
    // proszę projekt umowy", a odrzucić przypadkowe współwystąpienie na przeciwnych końcach zdania.
    private static readonly Regex VerbThenNoun = new(
        $@"\b{Verbs}\b(?<gap>(?:\s+\S+){{0,6}}?)\s+{Nouns}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // „wzór/szablon/projekt/draft umowy" — prośba o dokument niezależnie od czasownika.
    private static readonly Regex TemplateNoun = new(
        $@"\b(?:wz[óo]r|wzoru|szablon[u]?|projekt[u]?|draft)\s+{Nouns}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Słowo pytające w przerwie między czasownikiem a rzeczownikiem = pytanie badawcze, nie prośba
    // o dokument („napisz, CZYM różni się pozew od wniosku", „napisz, JAK wypowiedzieć umowę").
    private static readonly Regex QuestionWordInGap = new(
        @"\b(?:czym|jak(?:ie|a|i|ich)?|kiedy|dlaczego|czemu|gdzie|komu|ile|czego|co|czy|kto)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary><c>true</c> ⇒ użytkownik prosi o sporządzenie dokumentu (nie o wiedzę o nim).</summary>
    public static bool IsDraftingRequest(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (TemplateNoun.IsMatch(text)) return true;

        foreach (Match m in VerbThenNoun.Matches(text))
            if (!QuestionWordInGap.IsMatch(m.Groups["gap"].Value))
                return true;

        return false;
    }
}
