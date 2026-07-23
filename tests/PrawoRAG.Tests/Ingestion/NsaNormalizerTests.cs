using System.Text.Json;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Sources;
using PrawoRAG.Ingestion.Nsa;

namespace PrawoRAG.Tests.Ingestion;

/// <summary>
/// T-NSA — normalizer orzeczeń NSA/WSA (JuDDGES): metadane z SourcePayload, full_text jako treść,
/// sekcje sentencja/uzasadnienie, kanoniczny DocType=judgment (selektor „nsa-judgment"), courtType
/// NSA/WSA, prawomocność z pola finality. Payload wg realnych pól datasetu.
/// </summary>
public class NsaNormalizerTests
{
    private static RawDocument Raw(string fullText, object payload, string id = "/doc/6447E5BA57") => new()
    {
        Source = SourceKeys.Nsa,
        ExternalId = id,
        DocType = DocTypes.NsaJudgment,
        RawContent = fullText,
        ContentFormat = ContentFormats.PlainText,
        SourceUrl = "https://orzeczenia.nsa.gov.pl" + id,
        SourcePayload = JsonSerializer.SerializeToElement(payload),
    };

    private static object WsaPayload() => new
    {
        docket_number = "II SA/Op 8/05",
        court_name = "Wojewódzki Sąd Administracyjny w Opolu",
        judgment_type = "Wyrok WSA w Opolu",
        finality = "orzeczenie prawomocne",
        judgment_date = "2005-10-13T00:00:00+02:00",
        judges = new[] { "Grażyna Jeżewska", "Jerzy Krupiński" },
        keywords = new[] { "Policja" },
        case_type_description = new[] { "6192 Funkcjonariusze Policji" },
        challenged_authority = "Komendant Policji",
        extracted_legal_bases = new[] { "art. 52 ustawy o Policji" },
    };

    [Fact]
    public void Extracts_metadata_and_canonical_judgment_type()
    {
        var norm = new NsaNormalizer().Normalize(Raw("Sentencja wyroku.", WsaPayload()));

        Assert.Equal(DocTypes.Judgment, norm.DocType);          // zapisywany jako orzecznictwo
        Assert.Equal(SourceKeys.Nsa, norm.Source);
        Assert.Equal("II SA/Op 8/05", norm.Locator!.CaseNumber);
        Assert.Equal("Wojewódzki Sąd Administracyjny w Opolu", norm.Locator.Court);
        Assert.Equal(new DateOnly(2005, 10, 13), norm.Locator.JudgmentDate);
        Assert.Equal("WSA", norm.TypedMetadata["courtType"]);
        Assert.Equal(true, norm.TypedMetadata["prawomocne"]);
        Assert.Contains("Wyrok WSA w Opolu", norm.Title);
        Assert.Contains(Bases(norm), b => (b["text"] as string) == "art. 52 ustawy o Policji");
    }

    /// <summary>Podstawy prawne jako lista obiektów (kształt metadanych — nie płaskie stringi).</summary>
    private static List<Dictionary<string, object?>> Bases(PrawoRAG.Domain.Documents.NormalizedDocument norm) =>
        (List<Dictionary<string, object?>>)norm.TypedMetadata["referencedRegulations"]!;

    [Fact]
    public void Nsa_court_type_detected()
    {
        var norm = new NsaNormalizer().Normalize(Raw("Treść.", new
        {
            docket_number = "II FSK 1938/08",
            court_name = "Naczelny Sąd Administracyjny",
            judgment_type = "Wyrok NSA",
            finality = "orzeczenie prawomocne",
            judgment_date = "2010-04-02",
        }));

        Assert.Equal("NSA", norm.TypedMetadata["courtType"]);
        Assert.Equal(new DateOnly(2010, 4, 2), norm.Locator!.JudgmentDate);
    }

    [Fact] // sekcje: sentencja przed UZASADNIENIE, uzasadnienie po; nagłówek kontekstowy = sąd — sygnatura
    public void Splits_sentencja_and_uzasadnienie()
    {
        const string full = "Sąd oddalił skargę.\nUZASADNIENIE\nSkarżący wniósł o uchylenie decyzji, sąd uznał skargę za niezasadną.";
        var norm = new NsaNormalizer().Normalize(Raw(full, WsaPayload()));

        Assert.Equal(["sentencja", "uzasadnienie"], norm.Segments.Select(s => s.Label));
        Assert.StartsWith("Sąd oddalił", norm.Segments[0].Text);
        Assert.StartsWith("UZASADNIENIE", norm.Segments[1].Text);
        Assert.All(norm.Segments, s => Assert.Contains("II SA/Op 8/05", s.ContextHeader!));
    }

    [Fact] // brak markera → jeden segment „document"
    public void No_marker_single_segment()
    {
        var norm = new NsaNormalizer().Normalize(Raw("Krótkie postanowienie bez uzasadnienia.", WsaPayload()));
        Assert.Equal(["document"], norm.Segments.Select(s => s.Label));
    }

    [Fact] // pusty full_text → 0 segmentów + quality issue (dokument-widmo, chunker da 0 chunków)
    public void Empty_text_yields_no_segments_and_issue()
    {
        var norm = new NsaNormalizer().Normalize(Raw("", WsaPayload()));
        Assert.Empty(norm.Segments);
        Assert.Contains(norm.QualityIssues, i => i.Contains("full_text"));
    }

    [Fact] // nieprawomocny wyrok → prawomocne=false
    public void Nonfinal_judgment_flagged()
    {
        var norm = new NsaNormalizer().Normalize(Raw("Treść.", new
        {
            docket_number = "I SA/Wr 1/25", court_name = "Wojewódzki Sąd Administracyjny we Wrocławiu",
            judgment_type = "Wyrok WSA we Wrocławiu", finality = "orzeczenie nieprawomocne",
            judgment_date = "2025-02-01",
        }));
        Assert.Equal(false, norm.TypedMetadata["prawomocne"]);
    }

    [Fact] // wariant zgodnościowy: obiekt z samym polem „text"
    public void Legal_bases_as_text_objects_are_kept()
    {
        var norm = new NsaNormalizer().Normalize(Raw("Treść.", new
        {
            docket_number = "II SA/Op 8/05", court_name = "WSA w Opolu", judgment_type = "Wyrok WSA w Opolu",
            finality = "orzeczenie prawomocne", judgment_date = "2005-10-13",
            extracted_legal_bases = new[] { new { text = "art. 145 § 1 pkt 1 ppsa" } },
        }));
        Assert.Contains(Bases(norm), b => (b["text"] as string) == "art. 145 § 1 pkt 1 ppsa");
    }

    /// <summary>
    /// REGRESJA: realny kształt `extracted_legal_bases` w JuDDGES to obiekty {link, article, journal,
    /// law} — BEZ pola „text". Wcześniejsze czytanie przez StrArray (szuka wyłącznie „text") dawało
    /// pustą listę przy KAŻDYM orzeczeniu — 300/300 dokumentów smoke'a wylądowało bez przepisów.
    /// Test pilnuje, że pola źródłowe są zachowane i że powstaje wspólne, czytelne „text".
    /// </summary>
    [Fact]
    public void Legal_bases_in_real_juddges_shape_are_preserved()
    {
        var norm = new NsaNormalizer().Normalize(Raw("Treść.", new
        {
            docket_number = "IV SA/Po 655/24", court_name = "Wojewódzki Sąd Administracyjny w Poznaniu",
            judgment_type = "Wyrok WSA w Poznaniu", finality = "orzeczenie prawomocne",
            judgment_date = "2024-10-02",
            extracted_legal_bases = new[]
            {
                new
                {
                    link = "http://isap.sejm.gov.pl/DetailsServlet?id=WDU20230000977",
                    article = "art. 14 ust. 8, art. 28 ust. 1",
                    journal = "Dz.U. 2023 poz 977",
                    law = "Ustawa z dnia 27 marca 2003 r. o planowaniu i zagospodarowaniu przestrzennym (t. j.)",
                },
            },
        }));

        var b = Assert.Single(Bases(norm));
        Assert.Equal("Dz.U. 2023 poz 977", b["journal"]);
        Assert.Equal("art. 14 ust. 8, art. 28 ust. 1", b["article"]);
        Assert.Equal("http://isap.sejm.gov.pl/DetailsServlet?id=WDU20230000977", b["link"]);
        Assert.Contains("o planowaniu i zagospodarowaniu przestrzennym", (string)b["law"]!);
        // Wspólne pole „text" (parytet z SAOS): ustawa + dziennik + artykuły w jednym łańcuchu.
        var text = (string)b["text"]!;
        Assert.Contains("Dz.U. 2023 poz 977", text);
        Assert.Contains("art. 14 ust. 8", text);
    }
}
