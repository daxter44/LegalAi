using System.Text.RegularExpressions;

namespace PrawoRAG.Ingestion.Cleaning;

/// <summary>
/// Wycina z treści jednostki bibliograficzną historię nowelizacji — polska konwencja legislacyjna
/// dokleja przy pierwszym przywołaniu aktu przypis w rodzaju „(Dz.U. Nr 43, poz. 296; zm.: z 1965 r.
/// Nr 15, poz. 113; z 1974 r. Nr 27, poz. 157 i Nr 39, poz. 231, …)" albo „zmiany wymienionej ustawy
/// zostały ogłoszone w Dz. U. z …". Lista kilkudziesięciu numerów Dziennika Ustaw nie niesie treści
/// normatywnej, a bywa dłuższa niż właściwa treść chunka — dominuje wtedy embedding (zmierzone:
/// 14,7 tys. chunków aktów z ≥5 pozycjami, patrz PLAN-NAPRAWA-SZUMU-CHUNKOW-2026-08-28.md).
///
/// Zasada: ciąg ≥<see cref="MinItems"/> pozycji „poz. N" pooddzielanych wyłącznie separatorami
/// (przecinki, „i"/„oraz", „Nr X", „z RRRR r.", „zm.:") to historia zmian. Pierwszy adres publikacyjny
/// („Dz. U. z 1964 r. Nr 43, poz. 296") ZOSTAJE — to prawnie użyteczny adres aktu — chyba że blok
/// zaczyna się frazą „zmiany … ogłoszone w” (czysty przypis bez adresu pierwotnego): wtedy leci całość.
/// </summary>
public static class AmendmentFootnoteCleaner
{
    /// <summary>Minimalna liczba pozycji „poz. N" w ciągu, żeby uznać go za historię nowelizacji.</summary>
    public const int MinItems = 5;

    // „poz. 708" oraz wielokrotne „poz. 1494 i 1497" / „poz. 296, 300 i 305" — jedna pozycja z listą numerów.
    private static readonly Regex Item = new(
        @"poz\.\s*\d+(?:\s*(?:,|i\b|oraz\b)\s*\d+)*", RegexOptions.Compiled);

    // Tekst dozwolony MIĘDZY pozycjami tej samej historii zmian: separatory, spójniki, „Nr X",
    // rocznik, „zm.:", „z późn. zm.". Cokolwiek innego przerywa ciąg. Spójnik „i" bez \b, a odstępy
    // jako \s* — stare PDF-y Dz.U. mają sklejone spacje („iNr 281,poz. 2781", „wDz. U. z2004 r.").
    private static readonly Regex GapOnly = new(
        @"^(?:[\s,;.()\[\]–—-]|i|oraz|a\s*także|zm\.\s*:|z\s*późn\.\s*zm\.|Nr\s*\d+[a-z]?|z\s*\d{4}\s*r\.)*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Kontekst bezpośrednio PRZED pierwszą pozycją ciągu — dociągamy do usuwanego/zachowywanego
    // zakresu: opcjonalna fraza-przypis + „Dz. U." + rocznik + numery. Odstępy \s* (sklejone spacje
    // w starych PDF-ach: „zostałyogłoszone wDz. U."), z backtrackingiem \w+ rozdzielającym słowa.
    private static readonly Regex Prefix = new(
        @"(?<intro>zmian\w+\s*(?:tekstu\s*jednolitego\s*)?wymienion\w+\s*ustaw\w+\s*zosta\w+\s*ogłoszon\w+\s*w\s*)?" +
        @"(?:Dz\.\s*U\.\s*)?(?:z\s*\d{4}\s*r\.\s*)?(?:Nr\s*\d+[a-z]?\s*,?\s*)*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Ogon za ostatnią pozycją: „ , z późn. zm." / kropka — częścią historii, do usunięcia razem z nią.
    private static readonly Regex Suffix = new(
        @"^(?:\s*,?\s*z\s*późn\.\s*zm\.)?\s*\.?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Clean(string text)
    {
        var items = Item.Matches(text);
        if (items.Count < MinItems) return text;

        var result = text;
        // Od końca — usuwanie nie unieważnia wcześniejszych indeksów.
        foreach (var (first, last) in FindRuns(text, items).AsEnumerable().Reverse())
        {
            var runStart = items[first].Index;
            var runEnd = items[last].Index + items[last].Length;

            var prefix = Prefix.Match(text[..runStart]);
            var keepFirstItem = !prefix.Groups["intro"].Success; // brak frazy-przypisu = pierwszy adres zostaje
            var removeFrom = keepFirstItem
                ? items[first].Index + items[first].Length // tnij tuż za pierwszym adresem publikacyjnym
                : prefix.Index;                            // czysty przypis — tnij od frazy „zmiany…"
            var removeTo = runEnd + Suffix.Match(text[runEnd..]).Length;

            result = result.Remove(removeFrom, removeTo - removeFrom);
        }

        return result == text ? text : Tidy(result);
    }

    /// <summary>Ciągi kolejnych pozycji rozdzielonych wyłącznie separatorami, o długości ≥ MinItems.</summary>
    private static List<(int First, int Last)> FindRuns(string text, MatchCollection items)
    {
        var runs = new List<(int, int)>();
        var runStart = 0;
        for (var i = 1; i <= items.Count; i++)
        {
            var chained = i < items.Count && GapOnly.IsMatch(
                text[(items[i - 1].Index + items[i - 1].Length)..items[i].Index]);
            if (chained) continue;
            if (i - runStart >= MinItems) runs.Add((runStart, i - 1));
            runStart = i;
        }
        return runs;
    }

    /// <summary>Sprząta ślady po wycięciu: puste/urwane nawiasy, osierocone separatory, podwójne spacje.</summary>
    private static string Tidy(string text)
    {
        text = Regex.Replace(text, @"\(\s*[;,]?\s*\)", "");   // "( )" / "(;)" po wycięciu całego wnętrza
        text = Regex.Replace(text, @"[;,]\s*\)", ")");        // "…poz. 296; )" → "…poz. 296)"
        text = Regex.Replace(text, @"\(\s+", "(");
        text = Regex.Replace(text, @"\s+\)", ")");
        text = Regex.Replace(text, @"[ \t]{2,}", " ");
        return text;
    }
}
