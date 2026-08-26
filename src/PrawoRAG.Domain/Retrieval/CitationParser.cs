using System.Text.RegularExpressions;

namespace PrawoRAG.Domain.Retrieval;

/// <summary>Odwołanie do konkretnej jednostki aktu wyłuskane z pytania. <see cref="ActHint"/> to surowa
/// wskazówka aktu (fraza „kodeks…" albo skrót „KW") — rozpoznaniem na dokument zajmuje się osobny resolver.</summary>
public sealed record CitationRef(string Article, string? Paragraph, string? ActHint);

/// <summary>
/// Deterministyczny ekstraktor cytatów z pytania (QU-0, P1/P2/P4). Bez zależności, w pełni testowalny.
/// Numer artykułu ma małą wariancję → regex tolerancyjny (skróty, brak kropek). Nazwa aktu (skróty +
/// polska odmiana) → fraza „kodeks…" albo skrót; ostateczne rozpoznanie robi resolver (aliasy + pg_trgm).
/// </summary>
public static class CitationParser
{
    // „art.", „art", „artykuł/artykule/artykułu…" + numer (może mieć literę: „43bb", „175da").
    private static readonly Regex ArtRe =
        new(@"\bart(?:yku\w*|\.)?\s*(\d+[a-zA-Z]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Kolejne numery w wyliczeniu „art. 94 i 95", „art. 5, 6 oraz 7".
    private static readonly Regex ChainRe =
        new(@"\G\s*(?:,|i|oraz)\s*(\d+[a-zA-Z]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Jednostka niższego poziomu: „§ 2" (prawo polskie) albo „ust. 1" (prawo UE — rozporządzenia
    // i dyrektywy dzielą artykuł na USTĘPY, nie paragrafy). Po „ust" wymagana jest cyfra, więc „ustawa
    // o …" się nie łapie. Informacyjnie — retrieval i tak pobiera CAŁY artykuł (P3).
    private static readonly Regex ParaRe =
        new(@"(?:§\s*|\bust\.?\s*)(\d+[a-zA-Z]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Nazwy zwyczajowe aktów UE — dopasowanie po CAŁYM tokenie (nie fragmencie słowa), bo to
    // charakterystyczne skróty. Kanoniczne oznaczenie („2016/679") daje ActAliases.
    private static readonly Regex EuAliasRe = new(
        @"(?<![\p{L}])(RODO|GDPR|AI\s*Act|AIA|DSA|DMA|DORA|MiCA|NIS\s*2|MDR|REACH)(?![\p{L}])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Oznaczenie aktu UE podane wprost: „rozporządzenie (UE) 2016/679", „(WE) nr 45/2001",
    // „dyrektywa 95/46/WE". To dokładny identyfikator aktu, więc ma pierwszeństwo przed nazwą zwyczajową.
    private static readonly Regex EuDesignatorRe = new(
        @"\((?:UE|WE|EWG|Euratom|EWWiS)\)\s*(?:nr\s*)?(\d{1,4}/\d{2,4})"
        + @"|\b(\d{2,4}/\d{1,4}/(?:WE|EWG|UE|Euratom|EWWiS))\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Fraza aktu: „Kodeksu wykroczeń", „kodeks postępowania cywilnego" (kodeks + 1-2 słowa).
    private static readonly Regex KodeksRe =
        new(@"kodeks\w*(?:\s+\p{L}+){1,2}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Nazwa ustawy podana wprost — KORPUSOWO, bez listy: wyłuskujemy frazę, a rozpoznaniem na konkretny
    // akt zajmuje się fuzzy resolver (pg_trgm do REALNYCH tytułów w bazie). Nowa ustawa działa od razu po
    // zaindeksowaniu, zero dopisywania. Wysoka precyzja: „ustawa … o …" i „ordynacja …" to niemal zawsze
    // odwołanie do aktu (inaczej niż samo „prawo", które bywa „prawo do obrony" → świadomie pominięte,
    // bo fałszywy inject; wymaga pomiaru progu trigramów na korpusie). Fraza cięta na interpunkcji/„art.",
    // żeby nie połknąć reszty zdania; długość zdroworozsądkowo ograniczona (fuzzy jest tolerancyjny).
    // „ustaw(a|y|ie|ę|ą|…) … o …" — odmiana RZECZOWNIKA „ustawa" (nie „ustawodawca"/„ustawstwo") + „o"
    // wprowadzające nazwę. „o" jest wymagane, więc „art. 5 tej ustawy" (bez „o") NIE jest aktem.
    private static readonly Regex UstawaRe =
        new(@"\bustaw(?:a|y|ie|ę|ą|om|ami|ach)?\b[^.?!,;\n]*?\bo\b[^.?!,;\n]*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OrdynacjaRe =
        new(@"\bordynacj\w*(?:\s+\p{L}+){1,2}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ArtCutRe =
        new(@"\bart(?:yku\w*|\.)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Maks. długość wyłuskanej nazwy aktu (znaki) — dłuższa fraza to zwykle złapane pół zdania,
    /// a i tak trafia do tolerancyjnego fuzzy-matchu.</summary>
    private const int MaxActHintChars = 80;

    private static readonly Regex Ws = new(@"\s+", RegexOptions.Compiled);

    // Skróty kodeksów — litery mogą być rozdzielone kropką/spacją („k.p.c.", „KPC", „k. w."). Dłuższe pierwsze.
    private static readonly (string Norm, Regex Re)[] Abbrevs =
        new[] { "KPC", "KPK", "KKW", "KKS", "KRO", "KPA", "KSH", "KK", "KC", "KW", "KP" }
        .Select(a => (a, new Regex(@"(?<![\p{L}.])" + string.Join(@"\.?\s?", a.Select(ch => Regex.Escape(ch.ToString())))
            + @"\.?(?![\p{L}])", RegexOptions.Compiled | RegexOptions.IgnoreCase)))
        .ToArray();

    public static IReadOnlyList<CitationRef> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var articles = new List<(string No, int End)>();
        foreach (Match m in ArtRe.Matches(text))
        {
            articles.Add((m.Groups[1].Value, m.Index + m.Length));
            // Wyliczenie po numerze: „…94 i 95 oraz 96".
            var pos = m.Index + m.Length;
            for (var chain = ChainRe.Match(text, pos); chain.Success; chain = ChainRe.Match(text, chain.Index + chain.Length))
                articles.Add((chain.Groups[1].Value, chain.Index + chain.Length));
        }
        if (articles.Count == 0) return [];

        var actHint = ExtractActHint(text);
        var refs = new List<CitationRef>(articles.Count);
        for (var i = 0; i < articles.Count; i++)
        {
            // Paragraf informacyjnie: pierwszy § po pierwszym artykule (i tak pobieramy cały artykuł — P3).
            string? para = i == 0 && ParaRe.Match(text, articles[0].End) is { Success: true } p ? p.Groups[1].Value : null;
            refs.Add(new CitationRef(articles[i].No, para, actHint));
        }
        return refs;
    }

    /// <summary>
    /// Sama wskazówka aktu, BEZ wymogu artykułu („ustawa o ochronie danych osobowych", „ordynacja
    /// podatkowa", „KC"). <see cref="Parse"/> zwraca <see cref="CitationRef"/> tylko gdy jest artykuł —
    /// a <see cref="LegalTokenDetector"/> musi rozpoznać też gołą nazwę aktu jako odwołanie prawne.
    /// Publiczne, żeby nie powstała druga kopia tych wzorców (blizna: „rozjazd kopii = rozjazd metryki").
    /// </summary>
    public static string? ExtractActHint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var k = KodeksRe.Match(text);
        if (k.Success) return Ws.Replace(k.Value, " ").Trim();

        // Prawo UE PRZED skrótami kodeksów i przed frazą „ustawa o …": „art. 6 ust. 1 lit. f) RODO"
        // ma wskazać rozporządzenie 2016/679, a nie zgadywać polską ustawę o ochronie danych osobowych.
        // Oznaczenie podane wprost wygrywa z nazwą zwyczajową, bo jest dokładniejsze.
        if (EuDesignatorRe.Match(text) is { Success: true } d)
            return d.Groups[1].Success ? d.Groups[1].Value : d.Groups[2].Value;
        if (EuAliasRe.Match(text) is { Success: true } eu)
            return NormalizeEuAlias(eu.Groups[1].Value);

        foreach (var (norm, re) in Abbrevs)
            if (re.IsMatch(text)) return norm;
        // Nazwa ustawy/ordynacji wprost (korpusowo → fuzzy resolver). Po skrótach, żeby „art. 5 KC"
        // nadal dawało „KC", nie próbę łapania „ustawy".
        if (UstawaRe.Match(text) is { Success: true } u) return Clean(u.Value);
        if (OrdynacjaRe.Match(text) is { Success: true } o) return Clean(o.Value);
        return null;
    }

    /// <summary>Nazwa zwyczajowa aktu UE w formie, którą zna <see cref="ActAliases"/> („ai act" → „AI Act").
    /// Użytkownik pisze różnie („rodo", „AI act", „nis 2"), a mapa aliasów ma jedno brzmienie.</summary>
    private static string NormalizeEuAlias(string raw) => Ws.Replace(raw, " ").Trim().ToUpperInvariant() switch
    {
        "AI ACT" => "AI Act",
        "NIS 2" or "NIS2" => "NIS2",
        "MICA" => "MiCA",
        var other => other,
    };

    /// <summary>Normalizuje wyłuskaną nazwę aktu: obcina od „art." (gdyby fraza połknęła kolejny cytat),
    /// zwija białe znaki, przycina do <see cref="MaxActHintChars"/>.</summary>
    private static string Clean(string raw)
    {
        var cut = ArtCutRe.Match(raw) is { Success: true, Index: > 0 } a ? raw[..a.Index] : raw;
        var norm = Ws.Replace(cut, " ").Trim();
        return norm.Length > MaxActHintChars ? norm[..MaxActHintChars].Trim() : norm;
    }
}
