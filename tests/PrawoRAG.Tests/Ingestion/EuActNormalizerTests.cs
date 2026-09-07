using System.Text.Json;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Documents;
using PrawoRAG.Domain.Sources;
using PrawoRAG.Ingestion.EurLex;
using PrawoRAG.Tests.Fixtures;

namespace PrawoRAG.Tests.Ingestion;

/// <summary>
/// T-UE-3 — normalizacja aktów UE na REALNYCH dokumentach z CELLAR-a (pobrane 2026-08-26).
/// Dowodzi, że JEDEN normalizer obsługuje trzy warianty dokumentu zmierzone w populacji:
/// XHTML z kotwicami w dwóch odmianach markupu (Dz.U. UE „oj-*" i tekst skonsolidowany „*-norm")
/// oraz XHTML „legacy" BEZ żadnych identyfikatorów struktury (30% losowej próbki — starsze konwertery).
/// Pilnuje też trzech rzeczy, które psują korpus po cichu: znaczników wersji „▼M1" w środku zdania,
/// zgubionych załączników (wykaz z AI Act siedzi poza kontenerami artykułów) i bojlerplate'u
/// powtórzonego w całym korpusie. Bez sieci i bazy.
/// </summary>
public class EuActNormalizerTests
{
    private readonly EuActNormalizer _sut = new();

    private static RawDocument Doc(string fixture, string celex, string textCelex, string textVersion)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            celex, textCelex, textVersion, actClass = "substantive", language = "pol",
            amends = Array.Empty<string>(), repeals = Array.Empty<string>(),
        });

        return new RawDocument
        {
            Source = SourceKeys.EurLex,
            ExternalId = celex,
            DocType = DocTypes.EuAct,
            RawContent = EurLexFixtures.Read(fixture),
            ContentFormat = ContentFormats.Html,
            SourceUrl = $"https://eur-lex.europa.eu/legal-content/PL/TXT/?uri=CELEX:{celex}",
            SourcePayload = payload,
        };
    }

    private NormalizedDocument RodoBase() => _sut.Normalize(
        Doc(EurLexFixtures.RodoBase, "32016R0679", "32016R0679", "base"));

    private NormalizedDocument RodoConsolidated() => _sut.Normalize(
        Doc(EurLexFixtures.RodoConsolidated, "32016R0679", "02016R0679-20160504", "consolidated"));

    private NormalizedDocument AiAct() => _sut.Normalize(
        Doc(EurLexFixtures.AiActSlice, "32024R1689", "02024R1689-20260727", "consolidated"));

    private NormalizedDocument EPrivacy() => _sut.Normalize(
        Doc(EurLexFixtures.EPrivacyLegacy, "32002L0058", "02002L0058-20091219", "consolidated"));

    [Fact] // Selektor „eu-act", ale ZAPISUJEMY jako akt — w retrievalu prawo UE to akt prawny (wzorem NSA).
    public void Selector_is_eu_act_but_canonical_doc_type_is_act()
    {
        Assert.Equal(DocTypes.EuAct, _sut.DocType);
        Assert.Equal(DocTypes.Act, RodoBase().DocType);
    }

    [Fact] // RODO ma 99 artykułów — liczba z realnego dokumentu, więc regres w parsowaniu od razu widać.
    public void Finds_all_99_articles_in_both_markup_variants()
    {
        Assert.Equal(99, DistinctUnits(RodoBase(), "art. "));
        Assert.Equal(99, DistinctUnits(RodoConsolidated(), "art. "));
    }

    [Fact] // Podstawa przetwarzania z art. 6 ust. 1 lit. f) to WŁASNY chunk — nie sklejony z lit. a)-e).
    public void Article_6_paragraph_1_letter_f_is_its_own_segment()
    {
        var f = Assert.Single(RodoBase().Segments, s => s.Locator is { Article: "6", Paragraph: "1", Point: "f" });

        Assert.Contains("prawnie uzasadnionych interesów", f.Text);
        Assert.Equal("art. 6 ust. 1 lit. f)", f.Label);
        // Inne przesłanki legalności to INNE normy — nie mogą rozmywać wektora lit. f).
        Assert.DoesNotContain("wyraziła zgodę", f.Text);
        Assert.DoesNotContain("wykonania umowy", f.Text);
    }

    [Fact] // Ta sama norma musi wyjść z OBU wariantów markupu XHTML.
    public void Same_unit_is_found_in_consolidated_markup()
        => Assert.Contains("prawnie uzasadnionych interesów",
            Assert.Single(RodoConsolidated().Segments,
                s => s.Locator is { Article: "6", Paragraph: "1", Point: "f" }).Text);

    [Fact] // Nagłówek kontekstowy: nazwa zwyczajowa + rozdział + jednostka (chunk samoopisowy w cytacie).
    public void Context_header_carries_short_name_chapter_and_unit()
    {
        var f = RodoBase().Segments.First(s => s.Locator is { Article: "6", Paragraph: "1", Point: "f" });

        Assert.StartsWith("RODO,", f.ContextHeader);
        Assert.Contains("ROZDZIAŁ II", f.ContextHeader, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("art. 6 ust. 1 lit. f)", f.ContextHeader);
        Assert.StartsWith(f.ContextHeader, f.Text); // nagłówek wbity w treść chunka
    }

    [Fact] // TOR LEGACY: dokument bez ŻADNYCH kotwic (converter 7.6.2) — 30% losowej próbki populacji.
           // Bez tego toru e-Privacy (jedyna polska wersja to stara konsolidacja) nie wchodzi do korpusu.
    public void Parses_legacy_document_without_anchors()
    {
        var doc = EPrivacy();

        Assert.Equal(EuActNormalizer.ParsePath.LegacyText.ToString(), doc.TypedMetadata["parsePath"]);
        Assert.NotEmpty(doc.Segments);
        Assert.Contains("5", doc.Segments.Select(s => s.Locator?.Article));
        Assert.Contains(doc.QualityIssues, i => i.Contains("legacy"));
        // Zgoda na cookies (art. 5 ust. 3) to sedno tego aktu w praktyce.
        Assert.Contains(doc.Segments, s => s.Locator?.Article == "5" && s.Text.Contains("urządzeni", StringComparison.OrdinalIgnoreCase));
    }

    [Fact] // Wybór toru po ZAWARTOŚCI dokumentu, nie po roczniku ani klasie aktu.
    public void Detects_parse_path_from_content()
    {
        Assert.Equal(EuActNormalizer.ParsePath.Anchors,
            EuActNormalizer.DetectPath(EurLexFixtures.Read(EurLexFixtures.RodoBase), ContentFormats.Html));
        Assert.Equal(EuActNormalizer.ParsePath.LegacyText,
            EuActNormalizer.DetectPath(EurLexFixtures.Read(EurLexFixtures.EPrivacyLegacy), ContentFormats.Html));
        Assert.Equal(EuActNormalizer.ParsePath.PdfText,
            EuActNormalizer.DetectPath("Artykuł 1 Cokolwiek", ContentFormats.PdfText));
        Assert.Equal(EuActNormalizer.ParsePath.None,
            EuActNormalizer.DetectPath("<html><body><p>brak jednostek</p></body></html>", ContentFormats.Html));
    }

    [Fact] // Znaczniki wersji „▼M1"/„▼B" stoją w ŚRODKU zdania normy — w treści chunka być ich NIE MOŻE.
    public void Strips_consolidation_version_markers()
    {
        var segments = AiAct().Segments;

        Assert.NotEmpty(segments);
        Assert.All(segments, s => Assert.DoesNotContain('▼', s.Text));
    }

    [Fact] // ZAŁĄCZNIK III do AI Act (wykaz systemów wysokiego ryzyka) siedzi POZA kontenerami artykułów.
           // Parser oparty tylko na „art_*" gubi go w milczeniu — a to najczęściej pytana część tego aktu.
    public void Keeps_annex_with_its_own_locator()
    {
        var annex = AiAct().Segments.Where(s => s.Label?.StartsWith("załącznik", StringComparison.OrdinalIgnoreCase) == true).ToList();

        Assert.NotEmpty(annex);
        Assert.All(annex, s => Assert.Null(s.Locator?.Article)); // załącznik nie jest artykułem
        Assert.Contains(annex, s => s.Locator?.Anchor == "anx_III");
        Assert.Contains(annex, s => s.Text.Contains("wysokiego ryzyka", StringComparison.OrdinalIgnoreCase));
    }

    [Fact] // Nowelizacja wstawiła art. 75a-75d — sufiks literowy musi zostać w numerze, inaczej cytat kłamie.
    public void Keeps_articles_with_letter_suffix()
    {
        var articles = AiAct().Segments.Select(s => s.Locator?.Article).Distinct().ToList();

        Assert.Contains("5", articles);
        Assert.Contains("75a", articles);
    }

    [Fact] // Zakazane praktyki AI: lit. a) i c) to różne normy — każda własnym chunkiem.
    public void Splits_ai_act_prohibited_practices_into_letters()
    {
        var letters = AiAct().Segments
            .Where(s => s.Locator is { Article: "5", Paragraph: "1", Point: not null })
            .ToList();

        Assert.True(letters.Count >= 5, $"art. 5 ust. 1 ma wiele liter; znaleziono {letters.Count}");
        var a = letters.First(s => s.Locator!.Point == "a");
        Assert.Contains("techniki podprogowe", a.Text);
        Assert.DoesNotContain("scoring społeczny", a.Text);
    }

    [Fact] // Formuły końcowe powtórzone w całym korpusie (~12-13 tys. wektorów) nie stają się chunkami.
           // To ten sam mechanizm porażki, po którym powstał ChunkDegeneracy (1 056 chunków „(pominięty)").
    public void Filters_corpus_wide_boilerplate()
    {
        Assert.True(EuActNormalizer.IsBoilerplate(
            "Niniejsze rozporządzenie wchodzi w życie dwudziestego dnia po jego opublikowaniu w Dzienniku Urzędowym Unii Europejskiej."));
        Assert.True(EuActNormalizer.IsBoilerplate(
            "Niniejsze rozporządzenie wiąże w całości i jest bezpośrednio stosowane we wszystkich państwach członkowskich."));
        Assert.True(EuActNormalizer.IsBoilerplate("Sporządzono w Brukseli dnia 27 kwietnia 2016 r."));
        Assert.False(EuActNormalizer.IsBoilerplate(
            "Przetwarzanie jest zgodne z prawem wyłącznie w przypadkach, gdy spełniony jest co najmniej jeden z warunków."));

        Assert.DoesNotContain(RodoBase().Segments, s => s.Text.Contains("wiąże w całości i jest bezpośrednio stosowane"));
    }

    [Fact] // Etykieta jednostki, lokalizator i kotwica muszą się zgadzać — na nich stoi cytat i exact-match.
    public void Metadata_and_locator_are_consistent()
    {
        var doc = RodoConsolidated();

        Assert.Equal("32016R0679", doc.TypedMetadata["celex"]);
        Assert.Equal("02016R0679-20160504", doc.TypedMetadata["textCelex"]);
        Assert.Equal("consolidated", doc.TypedMetadata["textVersion"]);
        Assert.Equal("rozporządzenie", doc.TypedMetadata["euActType"]);
        Assert.Equal(2016, doc.TypedMetadata["year"]);
        Assert.Equal(true, doc.TypedMetadata["inForce"]);
        Assert.Equal("RODO", doc.TypedMetadata["shortTitle"]);
        Assert.Equal("9.17.0", doc.TypedMetadata["converterVersion"]); // diagnostyka zmian schematu CELLAR-a
        Assert.Equal("32016R0679", doc.Locator?.EliId);

        var f = doc.Segments.First(s => s.Locator is { Article: "6", Paragraph: "1", Point: "f" });
        Assert.Equal("art_6", f.Locator!.Anchor);
        Assert.Equal("RODO", f.Locator.DisplayAddress);
    }

    [Fact] // Tekst bazowy (brak polskiej konsolidacji) to sygnał jakości, nie cichy fakt.
    public void Reports_base_text_as_quality_issue()
    {
        Assert.Contains(RodoBase().QualityIssues, i => i.Contains("BAZOWY"));
        Assert.DoesNotContain(RodoConsolidated().QualityIssues, i => i.Contains("BAZOWY"));
    }

    [Fact] // Dokument bez jednostek nie wywala ingestii — pada GŁOŚNO, wpisem jakości (biała lista).
    public void Document_without_units_reports_quality_issue()
    {
        var raw = Doc(EurLexFixtures.RodoBase, "31962R0123", "31962R0123", "base") with
        {
            RawContent = "<html><body><p>Tekst bez jednostek.</p></body></html>",
        };

        var doc = _sut.Normalize(raw);

        Assert.Empty(doc.Segments);
        Assert.Contains(doc.QualityIssues, i => i.Contains("Nie znaleziono jednostek"));
        Assert.Contains(doc.QualityIssues, i => i.Contains("możliwa zmiana schematu CELLAR-a"));
    }

    private static int DistinctUnits(NormalizedDocument doc, string labelPrefix) => doc.Segments
        .Where(s => s.Label?.StartsWith(labelPrefix, StringComparison.Ordinal) == true)
        .Select(s => s.Locator?.Article)
        .Where(a => a is not null)
        .Distinct()
        .Count();
}
