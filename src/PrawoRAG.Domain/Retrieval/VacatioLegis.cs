using System.Text.RegularExpressions;

namespace PrawoRAG.Domain.Retrieval;

/// <summary>Jednostka wskazana w klauzuli wejścia w życie: artykuł + ewentualne punkty i litery
/// („art. 1 pkt 1 lit. a i c oraz pkt 3").</summary>
public sealed record VacatioTarget(string Article, IReadOnlyList<string> Points, IReadOnlyList<string> Letters)
{
    /// <summary>Znaczniki do RANKOWANIA chunków artykułu („pkt 1", „lit. c"). Akt nowelizujący ma zwykle
    /// jeden wielki artykuł pocięty na chunki po rozmiarze, bez granulacji pkt/lit w lokalizatorze —
    /// więc numerów używamy do wyboru KTÓRE chunki tego artykułu podać pierwsze, nie do ich adresowania.</summary>
    public IEnumerable<string> Markers() =>
        Points.Select(p => $"pkt {p}").Concat(Letters.Select(l => $"lit. {l}"));
}

/// <summary>
/// Klauzula wejścia w życie i wskazane w niej jednostki — funkcje czyste, bez sieci i bazy.
///
/// Powód istnienia (DIAGNOZA-NOWELIZACJA-DATA-WEJSCIA-W-ZYCIE-2026-08-27): pytanie „jakie zmiany wejdą
/// w życie we wrześniu 2026" trafia w klauzulę („z dniem 20 września 2026 r. wchodzą w życie art. 1
/// pkt 1 lit. a i c oraz pkt 3"), ale NIE w treść tych przepisów — zmierzone rangi treści art. 1 to
/// #2367, #50430 i #82405 przy DOKŁADNYM skanie, czyli trzy rzędy wielkości od okna kandydatów.
/// Nie jest to przypadek graniczny do dostrojenia: pytanie niesie datę, treść nowelizacji nie niesie
/// ŻADNEJ daty, a jedyny łącznik („art. 13 wskazuje art. 1 pkt 3") to CYTOWANIE WEWNĄTRZ DOKUMENTU.
/// Podobieństwo semantyczne tego związku nie odda, bo nie ma go czego mierzyć w przestrzeni wektorowej
/// — dokładnie jak przy moście cytowań dla orzeczeń, który dlatego działa strukturalnie.
/// </summary>
public static partial class VacatioLegis
{
    // „wchodzi/wchodzą w życie" — rdzeń klauzuli. Odmiana i dowolne białe znaki między słowami.
    [GeneratedRegex(@"wchodz(?:i|ą)\s+w\s+życie", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EntryIntoForceRegex();

    // „art. 1", „art 13", „artykuł 1a" — początek segmentu jednostki.
    [GeneratedRegex(@"\bart(?:ykuł\w*|\.)?\s*(\d+[a-zA-Z]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArticleRegex();

    // „pkt 1", „pkt 24" (w klauzulach nie ma kropki po „pkt").
    [GeneratedRegex(@"\bpkt\s*(\d+[a-zA-Z]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PointRegex();

    // „lit. a", „lit. c" oraz wyliczenia „lit. a i c", „lit. a, b oraz d" — druga i kolejne litery nie
    // powtarzają słowa „lit.", więc łapiemy całe wyliczenie jedną grupą.
    // `(?![\p{L}])` jest tu KONIECZNE, nie ozdobne: bez niego wyliczenie „lit. a i c oraz pkt 3" łykało
    // „oraz p" z „pkt" jako kolejną literę i do znaczników wchodziło „lit. p" (złapane testem).
    [GeneratedRegex(@"\blit\.?\s*([a-z](?![\p{L}])(?:\s*(?:,|i|oraz)\s*[a-z](?![\p{L}]))*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LetterRegex();

    /// <summary>
    /// Czy tekst jest klauzulą wejścia w życie WSKAZUJĄCĄ konkretne jednostki. Sama fraza „wchodzi
    /// w życie" nie wystarcza — pojawia się też w treści przepisów odsyłających do innych aktów i
    /// w formule końcowej („ustawa wchodzi w życie po upływie 14 dni"), z której nie ma czego dociągać.
    /// Warunkiem jest OBECNOŚĆ odwołań do jednostek, więc fałszywe rozpoznanie nic nie dokłada.
    /// </summary>
    public static bool IsEntryIntoForceClause(string? text) =>
        !string.IsNullOrWhiteSpace(text) && EntryIntoForceRegex().IsMatch(text) && ParseTargets(text).Count > 0;

    /// <summary>
    /// Jednostki wskazane w klauzuli. Segmentujemy po „art. N": wszystkie „pkt"/„lit." do NASTĘPNEGO
    /// „art." należą do bieżącego artykułu — tak działa składnia polskich klauzul („art. 1 pkt 1 lit. a
    /// i c oraz pkt 3, art. 5 pkt 2").
    /// </summary>
    public static IReadOnlyList<VacatioTarget> ParseTargets(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        // Czytamy DOPIERO OD frazy „wchodzi/wchodzą w życie". Powód praktyczny, złapany testem na realnej
        // klauzuli: chunk zaczyna się własnym nagłówkiem („Art. 13. Ustawa wchodzi w życie…" plus nagłówek
        // kontekstowy dodawany przez normalizer), więc parsowanie całości brało art. 13 za CEL i most
        // dociągałby samą klauzulę, zjadając sloty przeznaczone na treść zmian.
        // Świadoma granica: klauzula w rzadkim szyku „art. 1 pkt 3 wchodzi w życie z dniem…" (cel PRZED
        // frazą) nie zostanie rozpoznana — wtedy most nic nie dokłada, czyli zachowanie jak dotąd.
        var phrase = EntryIntoForceRegex().Match(text);
        if (!phrase.Success) return [];
        var scope = text[(phrase.Index + phrase.Length)..];

        var articles = ArticleRegex().Matches(scope);
        if (articles.Count == 0) return [];

        var targets = new List<VacatioTarget>();
        for (var i = 0; i < articles.Count; i++)
        {
            var article = articles[i].Groups[1].Value;
            var from = articles[i].Index + articles[i].Length;
            var to = i + 1 < articles.Count ? articles[i + 1].Index : scope.Length;
            var segment = scope[from..to];

            var points = PointRegex().Matches(segment).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var letters = LetterRegex().Matches(segment)
                .SelectMany(m => SplitLetters(m.Groups[1].Value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Ten sam artykuł może wystąpić kilka razy w klauzuli („art. 1 pkt 2 … oraz art. 1 pkt 7") —
            // scalamy, żeby dociąganie nie liczyło go dwa razy.
            var existing = targets.FindIndex(t => t.Article.Equals(article, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
                targets[existing] = new VacatioTarget(
                    article,
                    targets[existing].Points.Concat(points).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    targets[existing].Letters.Concat(letters).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            else
                targets.Add(new VacatioTarget(article, points, letters));
        }

        return targets;
    }

    /// <summary>
    /// „a i c" → [„a", „c"]; „a, b oraz d" → [„a", „b", „d"].
    ///
    /// Pułapka: spójnik „i" jest sam jednoliterowy, więc w wyliczeniu wpadałby jako litera „i".
    /// Traktujemy go jako separator, CHYBA że jest jedynym tokenem („lit. i" — rzadkie, ale legalne).
    /// Świadoma granica: „lit. a i i" zgubi drugą literę. Konsekwencja jest ograniczona do RANKOWANIA
    /// chunków (dociągamy cały wskazany artykuł, nie pojedynczą literę), więc nie ma tu ryzyka
    /// podania niewłaściwego przepisu — najwyżej kolejność chunków będzie mniej trafna.
    /// </summary>
    private static IEnumerable<string> SplitLetters(string raw)
    {
        var tokens = raw
            .Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .ToList();

        if (tokens.Count == 1) return tokens.Where(IsLetter);
        return tokens.Where(t => t is not ("i" or "oraz")).Where(IsLetter);

        static bool IsLetter(string s) => s.Length == 1 && char.IsAsciiLetterLower(s[0]);
    }
}
