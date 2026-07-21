using PrawoRAG.Domain.Privacy;

namespace PrawoRAG.Tests.Privacy;

/// <summary>
/// Detektor danych osobowych (nieblokujące ostrzeżenie UI): wzorce strukturalne z sumami kontrolnymi,
/// wysoka precyzja. Kluczowe: ZERO fałszywych alarmów na tekście prawnym (artykuły, sygnatury, lata, ELI).
/// Dane testowe to poprawne-formalnie fikcyjne numery (zweryfikowane sumy kontrolne), nie prawdziwe osoby.
/// </summary>
public class SensitiveDataDetectorTests
{
    [Fact]
    public void Detects_valid_pesel()
        => Assert.Contains(SensitiveDataKind.Pesel, SensitiveDataDetector.Detect("mój numer to 44051401359 z dowodu"));

    [Theory] // NIP goły i z separatorami 3-3-2-2
    [InlineData("NIP firmy 1234563218")]
    [InlineData("NIP 123-456-32-18 na fakturze")]
    public void Detects_valid_nip(string text)
        => Assert.Contains(SensitiveDataKind.Nip, SensitiveDataDetector.Detect(text));

    [Theory] // REGON 9- i 14-cyfrowy
    [InlineData("REGON 123456785")]
    [InlineData("REGON 12345678512347")]
    public void Detects_valid_regon(string text)
        => Assert.Contains(SensitiveDataKind.Regon, SensitiveDataDetector.Detect(text));

    [Fact]
    public void Detects_email()
        => Assert.Contains(SensitiveDataKind.Email, SensitiveDataDetector.Detect("proszę pisać na jan.kowalski@example.com"));

    [Theory] // telefon: +48 albo grupowanie separatorami
    [InlineData("tel. +48 601 234 567")]
    [InlineData("+48601234567")]
    [InlineData("dzwoń 601-234-567")]
    public void Detects_phone(string text)
        => Assert.Contains(SensitiveDataKind.Telefon, SensitiveDataDetector.Detect(text));

    [Fact]
    public void Detects_iban()
        => Assert.Contains(SensitiveDataKind.RachunekBankowy,
            SensitiveDataDetector.Detect("przelew na PL61109010140000071219812874"));

    [Fact]
    public void Detects_dowod_osobisty()
        => Assert.Contains(SensitiveDataKind.DowodOsobisty, SensitiveDataDetector.Detect("dowód ABA123456"));

    [Theory] // ZERO fałszywych alarmów na typowym tekście prawnym
    [InlineData("Jaka kara grozi z art. 415 § 2 k.c.?")]
    [InlineData("Co orzekł sąd w sprawie II CSK 123/45 z 2023 r.?")]
    [InlineData("Nowelizacja DU/2026/473 zmienia art. 631 KPC.")]
    [InlineData("Zgodnie z art. 118 kodeksu cywilnego termin wynosi 6 lat.")]
    public void No_false_positive_on_legal_text(string text)
        => Assert.Empty(SensitiveDataDetector.Detect(text));

    [Fact] // 11 cyfr z błędną sumą kontrolną → NIE PESEL
    public void Invalid_pesel_checksum_not_flagged()
        => Assert.Empty(SensitiveDataDetector.Detect("liczba 12345678901 nic nie znaczy"));

    [Fact] // gołe 9 cyfr bez separatorów → NIE telefon (chroni kwoty/liczby)
    public void Bare_nine_digits_not_phone()
        => Assert.DoesNotContain(SensitiveDataKind.Telefon, SensitiveDataDetector.Detect("kwota 601234567 groszy"));

    [Fact] // pusty/null → pusto
    public void Empty_input_yields_nothing()
    {
        Assert.Empty(SensitiveDataDetector.Detect(""));
        Assert.Empty(SensitiveDataDetector.Detect(null));
    }

    [Fact] // wiele kategorii naraz, stała kolejność (wg enuma), bez duplikatów
    public void Multiple_kinds_deduplicated_in_stable_order()
    {
        var hits = SensitiveDataDetector.Detect("PESEL 44051401359, NIP 1234563218, mail a@b.pl");
        Assert.Equal([SensitiveDataKind.Pesel, SensitiveDataKind.Nip, SensitiveDataKind.Email], hits);
    }
}
