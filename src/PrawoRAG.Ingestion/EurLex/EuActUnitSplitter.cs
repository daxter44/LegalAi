using System.Text.RegularExpressions;

namespace PrawoRAG.Ingestion.EurLex;

/// <summary>
/// Podział treści jednostki aktu UE (artykułu albo załącznika) na ustępy i litery/punkty.
/// JEDEN tor dla WSZYSTKICH źródeł treści, bo markup różni się zupełnie, a znaczniki jednostek
/// — po spłaszczeniu do linii — są identyczne: XHTML z kotwicami (tabele 4%/96% vs <c>grid-list</c>),
/// XHTML „legacy" bez kotwic (starsze konwertery CELLAR-a: 30% losowej próbki) i tekst z PDF (Faza 6).
/// Trzy parsery DOM zamiast tego znaczyłyby trzy zestawy pułapek do utrzymania.
///
/// Cel granulacji: „jeden wektor = jedna norma" (zmierzone na art. 52 § 1 KP — mieszanie podstaw
/// prawnych w jednym chunku rozmywa cosine o ~0,15 i zrzuca przepis z top rankingu).
/// </summary>
public static class EuActUnitSplitter
{
    /// <summary>Ustęp: linia zaczynająca się „1." / „12." (znaczniki stoją na POCZĄTKU linii po
    /// spłaszczeniu, więc odwołania w treści — „zgodnie z ust. 1." — nie łapią się).</summary>
    private static readonly Regex ParagraphMarker = new(@"^(\d{1,3})\.(?:\s+|$)", RegexOptions.Compiled);

    /// <summary>Litera: „a)", „ba)" (nowelizacje wstawiają litery dwuznakowe).</summary>
    private static readonly Regex LetterMarker = new(@"^([a-ząćęłńóśźż]{1,2})\)(?:\s+|$)", RegexOptions.Compiled);

    /// <summary>Punkt numerowany: „1)" — część aktów UE numeruje wyliczenia cyframi, nie literami.</summary>
    private static readonly Regex NumberedPointMarker = new(@"^(\d{1,3})\)(?:\s+|$)", RegexOptions.Compiled);

    /// <summary>Rodzaj jednostki niższego poziomu — decyduje o etykiecie cytatu („lit. f)" vs „pkt 1)").</summary>
    public enum PointKind { None, Letter, Number }

    /// <param name="Paragraph">Numer ustępu („1") albo null (treść poza ustępami).</param>
    /// <param name="Point">Numer litery/punktu („f", „1") albo null.</param>
    public sealed record Unit(string? Paragraph, string? Point, PointKind Kind, string Text);

    /// <summary>
    /// Dzieli spłaszczoną treść jednostki (bez nagłówka „Artykuł N"). Brak znaczników = jedna jednostka
    /// z całością (artykuł jednozdaniowy nie ma czego dzielić). Wstęp przed pierwszym znacznikiem
    /// (zdanie wprowadzające wyliczenie) zostaje OSOBNĄ jednostką: samodzielnie ma sens, a doklejony
    /// do każdej litery byłby bojlerplate'em wspólnym dla wszystkich (zmierzone: obniża cosine).
    /// </summary>
    public static List<Unit> Split(string unitText)
    {
        var units = new List<Unit>();
        if (string.IsNullOrWhiteSpace(unitText)) return units;

        var lines = unitText.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        string? paragraph = null, point = null;
        var kind = PointKind.None;
        var buffer = new List<string>();

        void Flush()
        {
            var text = string.Join("\n", buffer).Trim();
            buffer.Clear();
            if (text.Length > 0) units.Add(new Unit(paragraph, point, kind, text));
        }

        foreach (var line in lines)
        {
            if (ParagraphMarker.Match(line) is { Success: true } p)
            {
                Flush();
                paragraph = p.Groups[1].Value;
                point = null;
                kind = PointKind.None;
                AppendRest(buffer, line, p.Length);
                continue;
            }

            if (LetterMarker.Match(line) is { Success: true } l)
            {
                Flush();
                point = l.Groups[1].Value;
                kind = PointKind.Letter;
                AppendRest(buffer, line, l.Length);
                continue;
            }

            if (NumberedPointMarker.Match(line) is { Success: true } n)
            {
                Flush();
                point = n.Groups[1].Value;
                kind = PointKind.Number;
                AppendRest(buffer, line, n.Length);
                continue;
            }

            buffer.Add(line);
        }
        Flush();

        return units;
    }

    /// <summary>Etykieta jednostki w konwencji cytowania prawa UE: „art. 6 ust. 1 lit. f)",
    /// „załącznik III pkt 5 lit. b)".</summary>
    public static string Label(string? unit, string? paragraph, string? point, PointKind kind)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(unit)) parts.Add(unit);
        if (paragraph is not null) parts.Add($"ust. {paragraph}");
        if (point is not null) parts.Add(kind == PointKind.Number ? $"pkt {point})" : $"lit. {point})");
        return string.Join(" ", parts);
    }

    /// <summary>Treść linii po znaczniku (znacznik „1." bywa sam w linii — wtedy treść jest niżej).</summary>
    private static void AppendRest(List<string> buffer, string line, int markerLength)
    {
        var rest = line[markerLength..].Trim();
        if (rest.Length > 0) buffer.Add(rest);
    }
}
