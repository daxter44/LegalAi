using System.Text.RegularExpressions;
using PrawoRAG.Domain.Retrieval;

namespace PrawoRAG.Eval;

/// <summary>
/// Wyciąga z „podstawy prawnej" wykazu ministerialnego (np. „art. 63 ust. 3 ustawy z dnia
/// 29 lipca 2005 r. o przeciwdziałaniu narkomanii") artykuł + wskazówkę aktu do rozpoznania
/// w korpusie. UWAGA: warstwa tekstowa PDF gubi indeksy górne („art. 3310" = art. 33¹⁰),
/// dlatego artykuły porównujemy po normalizacji do znaków alfanumerycznych, nie 1:1.
/// </summary>
public static class ExamBasisParser
{
    public sealed record Basis(string? Article, string? ActAbbrev, string? UstawaHint, string Domain);

    private static readonly Regex ArtRe = new(@"\bart\.\s*(\d+\w*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Ogon „o <nazwie ustawy>" — najdłuższy sensowny fragment do dopasowania trigramowego tytułu.
    private static readonly Regex UstawaRe = new(@"ustaw[ay][^,]*?\b(o\s+.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Kolejność ma znaczenie: dłuższe skróty przed krótszymi („k.p.c." zawiera „k.c.").
    private static readonly (string Token, string Domain)[] Domains =
    [
        ("k.p.k.", "kpk"), ("k.p.c.", "kpc"), ("k.k.w.", "kkw"), ("k.k.s.", "kks"),
        ("k.r.o.", "kro"), ("k.s.h.", "ksh"), ("k.p.a.", "kpa"), ("k.w.", "kw"),
        ("k.p.", "kp"), ("k.k.", "kk"), ("k.c.", "kc"),
        ("konstytucj", "konstytucja"), ("traktat", "traktaty-ue"),
        ("prawo o ustroju", "ustrojowe"), ("ustaw", "ustawa-szczegolna"),
    ];

    public static Basis Parse(string? basis)
    {
        if (string.IsNullOrWhiteSpace(basis)) return new Basis(null, null, null, "brak");

        var article = ArtRe.Match(basis) is { Success: true } m ? m.Groups[1].Value : null;

        // Skrót kodeksu przez istniejący parser cytowań (rozumie „k.k.", „KPC" itd.).
        var abbrev = CitationParser.Parse(basis).FirstOrDefault()?.ActHint;

        var ustawa = UstawaRe.Match(basis) is { Success: true } u ? u.Groups[1].Value.TrimEnd('.', ' ') : null;

        var lower = basis.ToLowerInvariant();
        var domain = Domains.FirstOrDefault(d => lower.Contains(d.Token)).Domain ?? "inne";

        return new Basis(article, abbrev, ustawa, domain);
    }

    /// <summary>Porównanie numerów artykułów odporne na zgubione indeksy górne i formaty („63(1)" == „631").</summary>
    public static bool ArticleEquals(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return Norm(a) == Norm(b);
        static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }
}
