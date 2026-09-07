using PrawoRAG.Ingestion.Eli;
using PrawoRAG.Tests.Fixtures;

namespace PrawoRAG.Tests.Ingestion;

/// <summary>
/// T-ACT-UST — podział artykułów USTAW na ustępy (`unit_pass`) na realnym fixture ustawy
/// o ochronie praw lokatorów (DU/2001/733). Diagnoza 2026-08-31: ISAP oznacza ustępy ustaw
/// klasą `unit_pass` (nie `unit_para` = §), przez co 85% ustaw szło do bazy całymi artykułami —
/// art. 11 (12 ustępów, 4 różne przesłanki wypowiedzenia w jednym wektorze) przegrywał ranking
/// z krótkim, niewłaściwym art. 19p i model cytował ogólny KC zamiast lex specialis.
/// </summary>
public class ActNormalizerUstepTests
{
    private readonly ActNormalizer _sut = new();
    private PrawoRAG.Domain.Documents.NormalizedDocument Uopl() => _sut.Normalize(EliFixtures.LoadAct("DU/2001/733"));

    [Fact] // Art. 11 (12 ustępów) rozbity na segmenty per ustęp — koniec z jednym rozmytym wektorem
    public void Splits_article_11_into_ustep_segments()
    {
        var art11 = Uopl().Segments.Where(s => s.Locator?.Article == "11").ToList();
        Assert.True(art11.Count >= 8, $"Art. 11 ma 12 ustępów; segmentów: {art11.Count}");

        // Ustęp o wypowiedzeniu na piśmie (ust. 1) osobno od przesłanek z ust. 2
        var u1 = art11.Single(s => s.Locator!.Paragraph == "1" && s.Locator.Point is null);
        Assert.StartsWith("Art. 11 ust. 1", u1.Label);
        Assert.Contains("na piśmie", u1.Text);
    }

    [Fact] // Ustęp z punktami (ust. 2 pkt 1-5): kluczowa przesłanka zaległości czynszowej = WŁASNY segment
    public void Splits_ustep_2_into_points_and_isolates_rent_arrears()
    {
        var points = Uopl().Segments
            .Where(s => s.Locator?.Article == "11" && s.Locator.Paragraph == "2" && s.Locator.Point is not null)
            .ToList();
        Assert.True(points.Count >= 3, $"Art. 11 ust. 2 ma pkt 1-5; znaleziono {points.Count}");

        var pkt2 = points.Single(s => s.Locator!.Point == "2");
        Assert.Equal("Art. 11 ust. 2 pkt 2", pkt2.Label);
        Assert.Contains("trzy pełne okresy płatności", pkt2.Text); // próg lex specialis, o który poszło
        // Inne przesłanki nie mieszają się do wektora zaległości czynszowej:
        Assert.DoesNotContain("podnaj", pkt2.Text);      // pkt 3 (bezprawny podnajem)
        Assert.DoesNotContain("rozbiórk", pkt2.Text);    // pkt 4 (remont/rozbiórka)
    }

    [Fact] // Etykieta ustępu ustawy to "ust.", nie "§". Zakres: art. 11 — bo art. 26 (nowelizacja KC
           // wewnątrz tej ustawy) LEGALNIE zawiera § w cytowanych nowych brzmieniach przepisów KC.
    public void Ustawa_units_are_labeled_ust_not_paragraph_sign()
    {
        var art11 = Uopl().Segments.Where(s => s.Locator?.Article == "11" && s.Locator.Paragraph is not null).ToList();
        Assert.NotEmpty(art11);
        Assert.All(art11, s => Assert.DoesNotContain("§", s.Label!));
        Assert.All(art11, s => Assert.Contains(" ust. ", s.Label!));
    }

    [Fact] // Krótki artykuł bez ustępów (art. 1: przedmiot ustawy) zostaje w całości — bez regresji
    public void Short_article_without_usteps_stays_whole()
    {
        var art1 = Uopl().Segments.Where(s => s.Locator?.Article == "1").ToList();
        Assert.Single(art1);
        Assert.Null(art1[0].Locator!.Paragraph);
    }
}
