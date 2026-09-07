using PrawoRAG.Domain;
using PrawoRAG.Eval;

namespace PrawoRAG.Tests.Retrieval;

/// <summary>
/// Klasyfikator pozycji fused rankingu (CIT-2) — czysta funkcja, zero I/O. Sygnały wzięte z REALNEGO
/// dumpu PRZYPADEK-BUDOWLA-BUDYNEK-UPOL (sufiks „(wariant N/M)"/„(uchylony)" w etykiecie jednostki,
/// tytuł noweli „… o zmianie …", kształt punktu wyliczenia). Priorytet: uchylony → wariant → nowela →
/// cienkie-wyliczenie-aktu-bazowego → baza; flagi ortogonalne zliczają nakładki bez utraty informacji.
/// </summary>
public class ChunkClassifierTests
{
    private static ChunkClass Classify(string docType, string title, string? section, string text, int tokens = 100)
        => ChunkClassifier.Classify(new ChunkFacts(docType, title, section, text, tokens));

    [Fact] // orzeczenie rozstrzyga po DocType, niezależnie od reszty
    public void Judgment_by_doctype()
    {
        var c = Classify(DocTypes.Judgment, "Wyrok NSA z 2020 r.", "Uzasadnienie", "Sąd rozważył...");
        Assert.Equal(ChunkKind.Judgment, c.Kind);
        Assert.False(c.IsAct);
    }

    [Fact] // realna pozycja #2 z dumpu: u.p.o.l. Art. 22 (pominięty) — martwa treść
    public void Repealed_or_omitted_unit()
    {
        var c = Classify(DocTypes.Act, "Ustawa o podatkach i opłatach lokalnych", "Art. 22 (pominięty)", "Art. 22 (pominięty)");
        Assert.Equal(ChunkKind.RepealedOrOmitted, c.Kind);
        Assert.True(c.IsRepealedOrOmitted);
    }

    [Theory] // „(uchylony)" w różnych formach
    [InlineData("Art. 112a (uchylony)")]
    [InlineData("Art. 28 (pominięty)")]
    [InlineData("Art. 5 (uchylona)")]
    public void Repealed_variants(string section)
        => Assert.Equal(ChunkKind.RepealedOrOmitted, Classify(DocTypes.Act, "Ustawa X", section, "tekst").Kind);

    [Fact] // realna pozycja #18: nowela Ordynacja, Art. 1 § 1 pkt 1 (wariant 23/32) — near-duplikat
    public void Variant_defeats_dedup()
    {
        var c = Classify(DocTypes.Act, "Ustawa o zmianie ustawy - Ordynacja podatkowa",
            "Art. 1 § 1 pkt 1 (wariant 23/32)", "Art. 1 § 1 pkt 1\n1) organu podatkowego wyższego stopnia", tokens: 20);
        Assert.Equal(ChunkKind.AmendmentVariant, c.Kind); // wariant WYGRYWA z nowelą i cienkim wyliczeniem
        Assert.True(c.IsVariant);
        Assert.True(c.IsAmendmentAct); // flaga ortogonalna: to TEŻ nowela
        Assert.True(c.IsEnumerationPoint && c.IsThin); // i TEŻ cienki punkt — nakładka widoczna we flagach
    }

    [Fact] // nowela nie-wariant: tytuł „o zmianie", treść nie jest cienkim punktem
    public void Amendment_act_non_variant()
    {
        var c = Classify(DocTypes.Act, "Ustawa z 2002 r. o zmianie ustawy o podatkach i opłatach lokalnych",
            "Art. 1c", "Art. 1c\nW ustawie zmienia się następujące przepisy dotyczące zwolnień podatkowych oraz definicji obiektu.", tokens: 60);
        Assert.Equal(ChunkKind.AmendmentAct, c.Kind);
        Assert.True(c.IsAmendmentAct);
        Assert.False(c.IsVariant);
    }

    [Fact] // cienki punkt wyliczenia AKTU BAZOWEGO (nie noweli) → klasa CIT-4
    public void Thin_enumeration_of_base_act()
    {
        var c = Classify(DocTypes.Act, "Ustawa - Ordynacja podatkowa", "Art. 59 § 1 pkt 1",
            "Art. 59 § 1 pkt 1\n1) zapłaty,", tokens: 8);
        Assert.Equal(ChunkKind.ThinEnumeration, c.Kind);
        Assert.True(c.IsEnumerationPoint);
        Assert.True(c.IsThin);
    }

    [Fact] // punkt wyliczenia, ale DŁUGI (powyżej progu) → nie „cienki", spada do bazy
    public void Long_enumeration_point_is_not_thin()
    {
        var c = Classify(DocTypes.Act, "Ustawa X", "Art. 3 pkt 1",
            "Art. 3 pkt 1\n1) rozbudowana definicja pojęcia...", tokens: ChunkClassifier.ThinTokenThreshold + 20);
        Assert.Equal(ChunkKind.BaseAct, c.Kind);
        Assert.True(c.IsEnumerationPoint);
        Assert.False(c.IsThin);
    }

    [Fact] // klasa docelowa: definicja w akcie bazowym (art. 1a u.p.o.l. — cel sondy, 436 tok.)
    public void Base_act_definition_is_target_class()
    {
        var c = Classify(DocTypes.Act, "Ustawa o podatkach i opłatach lokalnych", "Art. 1a",
            "Art. 1a\nbudynek - obiekt wzniesiony w wyniku robót budowlanych wraz z instalacjami...", tokens: 436);
        Assert.Equal(ChunkKind.BaseAct, c.Kind);
        Assert.True(c.IsAct);
        Assert.False(c.IsAmendmentAct);
        Assert.False(c.IsVariant);
        Assert.False(c.IsThin);
    }

    [Fact] // brak sekcji (null) nie wywraca klasyfikacji
    public void Null_section_is_safe()
        => Assert.Equal(ChunkKind.BaseAct, Classify(DocTypes.Act, "Ustawa X", null, "Art. 1\ntreść przepisu", tokens: 50).Kind);
}
