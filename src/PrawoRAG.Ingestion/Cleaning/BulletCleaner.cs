using System.Text.RegularExpressions;

namespace PrawoRAG.Ingestion.Cleaning;

/// <summary>
/// Usuwa dekoracyjne markery list („⚫", „●", „•", …) z tekstu chunka — w HTML uzasadnień SAOS to
/// osobne linie-wypunktowania, które jako tokeny zaśmiecają embedding (komentarz przy
/// <c>ChunkerOptions.MinSubstantiveWords</c>: anomalnie wysokie cosine do każdego zapytania).
/// Ścieżkę ingestu chroni analogiczny strip w <c>Saos.HtmlText</c>; ta klasa służy backfillowi
/// istniejących chunków i testom.
/// </summary>
public static class BulletCleaner
{
    private static readonly Regex Glyphs = new(@"[⚫●•▪◦⬤]", RegexOptions.Compiled);
    private static readonly Regex TrailingSpaces = new(@"[ \t]+\n", RegexOptions.Compiled);
    private static readonly Regex BlankRuns = new(@"\n{3,}", RegexOptions.Compiled);
    private static readonly Regex MultiSpace = new(@"[ \t]{2,}", RegexOptions.Compiled);

    public static bool LooksAffected(string text) => Glyphs.IsMatch(text);

    public static string Clean(string text)
    {
        var cleaned = Glyphs.Replace(text, "");
        cleaned = TrailingSpaces.Replace(cleaned, "\n"); // po zdjęciu markera zostaje linia ze spacjami
        cleaned = BlankRuns.Replace(cleaned, "\n\n");
        return MultiSpace.Replace(cleaned, " ");
    }
}
