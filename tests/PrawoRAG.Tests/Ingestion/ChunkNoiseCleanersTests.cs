using PrawoRAG.Ingestion.Cleaning;

namespace PrawoRAG.Tests.Ingestion;

/// <summary>
/// Czyszczenia szumu chunków (PLAN-NAPRAWA-SZUMU-CHUNKOW-2026-08-28.md). Przykłady mojibake to
/// dosłowne fragmenty z żywej bazy (rozporządzenia DU 2001–2008 z toru PDF).
/// </summary>
public class MojibakeTranscoderTests
{
    [Theory]
    // Realne fragmenty z bazy (DU/2002/1353, DU/2003/2072, DU/2002/124, DU/2005/2202, DU/2006/1598):
    [InlineData("raport bie˝àcy po-winien zawieraç", "raport bieżący powinien zawierać")]
    [InlineData("podj´cia uchwa∏y", "podjęcia uchwały")]
    [InlineData("d∏u˝nych papierów wartoÊciowych", "dłużnych papierów wartościowych")]
    [InlineData("Je˝e-li jednak szlak ˝eglowny", "Jeżeli jednak szlak żeglowny")]
    [InlineData("Do zadaƒ Komisji nale˝y", "Do zadań Komisji należy")]
    [InlineData("dêwi´ku", "dźwięku")]
    [InlineData("informowaç na piÊmie Wydzia∏ Wydawnictw", "informować na piśmie Wydział Wydawnictw")]
    [InlineData("pod obcià˝eniem", "pod obciążeniem")]
    public void Fix_naprawia_realne_fragmenty_z_bazy(string broken, string expected)
        => Assert.Equal(expected, MojibakeTranscoder.Fix(broken));

    [Fact]
    public void LooksAffected_wykrywa_kazdy_ze_znakow_sygnatury()
    {
        // Sygnatura = odpowiedniki ł/ś/ż/ń (najczęstsze polskie diakrytyki) — każdy realnie
        // uszkodzony dokument zawiera choć jeden; fragmenty bez sygnatury (np. samo „dêwi´ku")
        // naprawia dopiero kontekst dokumentu, który ją niesie.
        Assert.True(MojibakeTranscoder.LooksAffected("uchwa∏y"));
        Assert.True(MojibakeTranscoder.LooksAffected("wartoÊciowych"));
        Assert.True(MojibakeTranscoder.LooksAffected("Je˝eli"));
        Assert.True(MojibakeTranscoder.LooksAffected("zadaƒ"));
        Assert.False(MojibakeTranscoder.LooksAffected("dêwi´ku")); // ê/´ bywają legalne — same nie wystarczą
    }

    [Theory]
    // Â→Ś potwierdzone w danych ("piÊmie"/"Â" w sweepie); reszta wielkich z kompletu map
    // Mac-CE↔MacRoman (ten sam bajt: Ś=0xE5, Ł=0xFC, Ą=0x84, Ź=0x8F, Ż=0xFB, Ć=0x8C, Ę=0xA2, Ń=0xC1).
    [InlineData("ÂciÊle okreÊlone", "Ściśle określone")]
    [InlineData("¸àcznie z za∏àcznikiem", "Łącznie z załącznikiem")]
    [InlineData("èród∏a i ˚eglarze", "Źródła i Żeglarze")]
    public void Fix_mapuje_wielkie_litery(string broken, string expected)
        => Assert.Equal(expected, MojibakeTranscoder.Fix(broken));

    [Fact]
    public void FixIfAffected_nie_dotyka_tekstu_bez_sygnatury()
    {
        // Legalne obce znaki (é, à) w zdrowym tekście — brak sygnatury [∏Ê˝ƒ] → zero zmian.
        const string healthy = "attaché kulturalny à propos exposé premiera";
        Assert.False(MojibakeTranscoder.LooksAffected(healthy));
        Assert.Same(healthy, MojibakeTranscoder.FixIfAffected(healthy));
    }

    [Fact]
    public void Fix_skleja_przeniesienia_wyrazow_ale_nie_zakresy_liczbowe()
    {
        var result = MojibakeTranscoder.Fix("statek wyprzedzajàcy po-winien wy-przedzaç w latach 2001-2002");
        Assert.Equal("statek wyprzedzający powinien wyprzedzać w latach 2001-2002", result);
    }
}

public class AmendmentFootnoteCleanerTests
{
    [Fact]
    public void Clean_zachowuje_pierwszy_adres_publikacyjny_a_wycina_historie_zmian()
    {
        // Wzorzec z diagnozy (chunk 467 tokenów — k.p.c. cytowany w wyroku TK): pierwszy adres zostaje,
        // ogon „zm.: …" (tu 6 pozycji) leci.
        const string input = "1. Art. 1 ustawy z dnia 17 listopada 1964 r. – Kodeks postępowania cywilnego " +
            "(Dz.U. Nr 43, poz. 296; zm.: z 1965 r. Nr 15, poz. 113; z 1974 r. Nr 27, poz. 157, Nr 39, poz. 231; " +
            "z 1975 r. Nr 45, poz. 234; z 1982 r. Nr 11, poz. 82 i Nr 30, poz. 210; z 1983 r. Nr 5, poz. 33), " +
            "rozumiany w ten sposób, iż w zakresie pojęcia \"sprawy cywilnej\" nie mogą się mieścić roszczenia " +
            "dotyczące zobowiązań pieniężnych, jest niezgodny z art. 45 ust. 1 Konstytucji.";

        var result = AmendmentFootnoteCleaner.Clean(input);

        Assert.Contains("(Dz.U. Nr 43, poz. 296)", result);      // adres pierwotny przetrwał
        Assert.DoesNotContain("poz. 113", result);                // historia zmian wycięta
        Assert.DoesNotContain("zm.:", result);
        Assert.Contains("rozumiany w ten sposób", result);        // treść normatywna nietknięta
        Assert.Contains("niezgodny z art. 45 ust. 1", result);
    }

    [Fact]
    public void Clean_wycina_caly_przypis_gdy_zaczyna_sie_fraza_o_ogloszonych_zmianach()
    {
        // Wariant „treść przypisu" — nie ma adresu pierwotnego do zachowania, leci całość z frazą.
        const string input = "Art. 15 stosuje się odpowiednio. Zmiany wymienionej ustawy zostały ogłoszone w " +
            "Dz. U. z 1965 r. Nr 15, poz. 113, z 1974 r. Nr 27, poz. 157 i Nr 39, poz. 231, z 1975 r. Nr 45, " +
            "poz. 234, z 1982 r. Nr 11, poz. 82 oraz z 1983 r. Nr 5, poz. 33. Przepis wchodzi w życie po 14 dniach.";

        var result = AmendmentFootnoteCleaner.Clean(input);

        Assert.DoesNotContain("poz.", result);
        Assert.DoesNotContain("Zmiany wymienionej ustawy", result);
        Assert.Contains("Art. 15 stosuje się odpowiednio.", result);
        Assert.Contains("Przepis wchodzi w życie po 14 dniach.", result);
    }

    [Fact]
    public void Clean_nie_rusza_pozycji_rozproszonych_w_tresci_normatywnej()
    {
        // 5 pozycji, ale każda w osobnym zdaniu merytorycznym — to nie jest historia zmian.
        const string input = "W ustawie ogłoszonej w Dz. U. poz. 100 stosuje się przepisy o rejestrach. " +
            "Rozporządzenie z Dz. U. poz. 200 określa wzory formularzy. Ustawa z Dz. U. poz. 300 dotyczy opłat. " +
            "Akt opublikowany w Dz. U. poz. 400 reguluje nadzór. Nowela z Dz. U. poz. 500 zmienia terminy.";

        Assert.Equal(input, AmendmentFootnoteCleaner.Clean(input));
    }

    [Fact]
    public void Clean_radzi_sobie_z_pozycjami_wielokrotnymi_i_sklejonymi_spacjami()
    {
        // Realny wzorzec z dry-runu na bazie (DU 2007, ustawa o powszechnym obowiązku obrony):
        // „poz. 708 i 711" (pozycja wielokrotna) + sklejone spacje ze starego PDF („wDz. U. z2004 r.").
        const string input = "Polskiej (Dz. U. z 2004 r. Nr 241, poz. 2416, z późn. zm.1)Zmiany tekstu jednolitego " +
            "wymienionej ustawy zostały ogłoszone wDz. U. z2004 r. Nr 277, poz. 2742, z 2005 r. Nr 180, poz. 1496, " +
            "z 2006 r. Nr 104, poz. 708 i 711 iNr 220, poz. 1600, z 2007 r. Nr 107, poz. 732 i Nr 176, poz. 1242) " +
            "w art. 76 po ust. 8a dodaje się ust. 8b w brzmieniu:";

        var result = AmendmentFootnoteCleaner.Clean(input);

        Assert.Contains("(Dz. U. z 2004 r. Nr 241, poz. 2416", result); // adres pierwotny zostaje
        Assert.DoesNotContain("poz. 2742", result);                     // historia (6 pozycji) wycięta w całości
        Assert.DoesNotContain("poz. 1600", result);
        Assert.DoesNotContain("Zmiany tekstu jednolitego", result);
        Assert.Contains("w art. 76 po ust. 8a dodaje się ust. 8b", result);
    }

    [Fact]
    public void Clean_nie_rusza_krotkiej_listy_ponizej_progu()
    {
        const string input = "(Dz. U. Nr 16, poz. 93, z 1971 r. Nr 27, poz. 252 oraz z 1976 r. Nr 19, poz. 122)";
        Assert.Equal(input, AmendmentFootnoteCleaner.Clean(input)); // 3 pozycje < MinItems=5
    }
}

public class BulletCleanerTests
{
    [Fact]
    public void Clean_usuwa_markery_samodzielne_i_srodliniowe()
    {
        // Wzorzec z bazy (uzasadnienia SAOS): marker jako osobna linia między pozycjami wyliczenia.
        const string input = "zasądza od pozwanego:\n⚫\nkwoty 5.000 zł tytułem zwrotu kosztów;\n⚫\nkwoty 55.000 zł tytułem zysków.";

        var result = BulletCleaner.Clean(input);

        Assert.DoesNotContain("⚫", result);
        Assert.Contains("kwoty 5.000 zł", result);
        Assert.Contains("kwoty 55.000 zł", result);
        Assert.DoesNotContain("\n\n\n", result); // po zdjęciu markerów bez potrójnych pustych linii
    }

    [Fact]
    public void Clean_nie_dotyka_zdrowego_tekstu()
    {
        const string healthy = "wyrok z dnia 5 lutego 2002 r., sygn. II CKN 1143/00";
        Assert.False(BulletCleaner.LooksAffected(healthy));
        Assert.Equal(healthy, BulletCleaner.Clean(healthy));
    }
}
