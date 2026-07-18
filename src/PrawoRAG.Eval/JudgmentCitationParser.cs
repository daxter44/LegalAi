using System.Text.RegularExpressions;

namespace PrawoRAG.Eval;

/// <summary>Cytat przepisu wyłuskany z TEKSTU ORZECZENIA: numer artykułu + skrót kodeksu
/// PRZYLEGAJĄCY do numeru („art. 415 k.c."). <see cref="Alias"/> null = artykuł bez
/// rozpoznanego aktu obok (nie zgadujemy — w orzeczeniach jeden akapit cytuje wiele kodeksów).</summary>
public sealed record JudgmentCitation(string Article, string? Alias);

/// <summary>
/// Parser cytowań w stylu uzasadnień sądowych — inny problem niż <c>CitationParser</c> (QU-0):
/// tam JEDNO pytanie wskazuje JEDEN akt (hint per tekst); tu jeden akapit orzeczenia cytuje
/// naprzemiennie k.c., k.p.c. i ustawy, więc akt wolno przypisać TYLKO, gdy jego skrót stoi
/// bezpośrednio po numerze („art. 415 § 1 k.c.", „art. 233 kpc", „art. 24 kodeksu cywilnego").
/// Precyzja ponad recall: artykuł bez przylegającego skrótu zostaje nieprzypisany (Alias=null),
/// nigdy nie jest zgadywany z sąsiedztwa.
/// </summary>
public static partial class JudgmentCitationParser
{
    // Skróty kodeksów — dłuższe PRZED krótszymi (k.p.c. nie może dopasować się jako k.p. + „c").
    // Litery mogą być rozdzielone kropką/spacją: „k.p.c.", „kpc", „K. p. c.".
    private static readonly string[] Aliases = ["KPW", "KKW", "KKS", "KRO", "KPA", "KSH", "KPC", "KPK", "KC", "KK", "KW", "KP"];

    private static readonly Regex CiteRe = BuildRegex();

    private static Regex BuildRegex()
    {
        // np. dla KPC: k\.?\s?p\.?\s?c\.? — z twardą granicą po ostatniej literze (żeby „kw" nie łapało „kwota").
        var abbrevAlt = string.Join("|", Aliases.Select(a =>
            "(?<" + a + ">" + string.Join(@"\.?\s?", a.Select(ch => Regex.Escape(ch.ToString()))) + @"\.?(?![\p{L}]))"));

        // Pełna fraza: „kodeksu cywilnego", „kodeks postępowania cywilnego" (kodeks + 1-2 słowa).
        const string phrase = @"(?<PHRASE>kodeks\w*(?:\s+\p{L}+){1,2})";

        // art. NUMER [§ N] [ust. N] [pkt N] [SKRÓT|FRAZA]
        // Numer może mieć doklejony indeks górny zapisany płasko („3851" = 385¹) — nierozstrzygalne
        // na poziomie tekstu, zostawiamy jak jest (sonda i tak grupuje po dosłownym numerze).
        var pattern =
            @"\bart(?:yku\w*|\.)?\s*(?<NO>\d+[a-ząćęłńóśźż]*)" +
            @"(?:\s*§\s*\d+[a-z]*)?(?:\s*ust\.?\s*\d+[a-z]*)?(?:\s*pkt\s*\d+[a-z]*)?" +
            @"\s*(?:" + abbrevAlt + "|" + phrase + ")?";

        return new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
    }

    public static IReadOnlyList<JudgmentCitation> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var result = new List<JudgmentCitation>();
        foreach (Match m in CiteRe.Matches(text))
        {
            var article = m.Groups["NO"].Value;
            string? alias = Aliases.FirstOrDefault(a => m.Groups[a].Success);
            if (alias is null && m.Groups["PHRASE"].Success)
                alias = MapPhrase(m.Groups["PHRASE"].Value);
            result.Add(new JudgmentCitation(article, alias?.ToUpperInvariant()));
        }
        return result;
    }

    /// <summary>Fraza „kodeks(u) …" → skrót po słowach kluczowych odmiany (cywiln-, karn-, …).
    /// Null = kodeks spoza mapy (np. „kodeks morski") — świadomie nieprzypisany.</summary>
    private static string? MapPhrase(string phrase)
    {
        var p = phrase.ToLowerInvariant();
        var proc = p.Contains("postępowania") || p.Contains("postepowania");
        if (p.Contains("cywiln")) return proc ? "KPC" : "KC";
        if (p.Contains("wykroczeń") || p.Contains("wykroczen")) return proc ? "KPW" : "KW";
        if (p.Contains("karn"))
            return proc ? "KPK" : p.Contains("wykonawcz") ? "KKW" : p.Contains("skarbow") ? "KKS" : "KK";
        if (p.Contains("prac")) return "KP";
        if (p.Contains("spółek") || p.Contains("spolek")) return "KSH";
        if (p.Contains("rodzinn")) return "KRO";
        if (p.Contains("administracyjn")) return "KPA";
        return null;
    }
}
