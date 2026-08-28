using PrawoRAG.Domain.Retrieval;

namespace PrawoRAG.Tests.Retrieval;

/// <summary>
/// Detektor prośby o sporządzenie dokumentu (Horyzont 0 draftingu, rozmowa 2026-08-28).
/// Kontrakt asymetryczny: fałszywy negatyw = dzisiejsze zachowanie (tani), fałszywy pozytyw =
/// odpowiedź wymogami zamiast zwykłej (łagodny, ale mylący przy prośbie o poradę) — stąd
/// detektor konserwatywny i testy pilnujące obu stron.
/// </summary>
public class DraftingRequestDetectorTests
{
    [Theory]
    // Tryb rozkazujący + rzeczownik dokumentu — rdzeń przypadków z rozmowy koncepcyjnej.
    [InlineData("Przygotuj umowę najmu mieszkania")]
    [InlineData("napisz wezwanie do zapłaty za fakturę 123")]
    [InlineData("Sporządź pozew o zapłatę przeciwko dłużnikowi")]
    [InlineData("stwórz regulamin sklepu internetowego")]
    [InlineData("zredaguj wypowiedzenie umowy o pracę")]
    // Okno między czasownikiem a rzeczownikiem (grzeczności, dopełnienia).
    [InlineData("przygotuj mi proszę projekt umowy o dzieło")]
    // Prośba przez pytanie o przysługę.
    [InlineData("czy możesz przygotować umowę pożyczki?")]
    [InlineData("mógłbyś napisać odwołanie od decyzji ZUS?")]
    // Prośba rzeczownikowa.
    [InlineData("proszę o przygotowanie pełnomocnictwa dla żony")]
    // Wzór/szablon — bez czasownika sporządzania.
    [InlineData("wzór umowy najmu okazjonalnego")]
    [InlineData("masz szablon wezwania do zapłaty?")]
    [InlineData("potrzebuję wzoru wypowiedzenia najmu")]
    public void Wykrywa_prosby_o_dokument(string question)
        => Assert.True(DraftingRequestDetector.IsDraftingRequest(question));

    [Theory]
    // Pytania badawcze o dokumenty — MUSZĄ zostać na zwykłej ścieżce.
    [InlineData("co powinna zawierać umowa najmu?")]
    [InlineData("jakie są skutki niezapłacenia wezwania do zapłaty?")]
    [InlineData("kiedy przedawnia się roszczenie z umowy?")]
    [InlineData("napisz, czym różni się pozew od wniosku")]
    [InlineData("napisz, jak wypowiedzieć umowę najmu")]
    // Prośba o poradę, nie o pismo.
    [InlineData("potrzebuję porady w sprawie umowy z deweloperem")]
    // Small-talk i pytania o system.
    [InlineData("cześć, co potrafisz?")]
    [InlineData("dzięki za pomoc")]
    // Puste/brzegowe.
    [InlineData("")]
    [InlineData("   ")]
    public void Nie_wykrywa_pytan_badawczych_ani_small_talku(string question)
        => Assert.False(DraftingRequestDetector.IsDraftingRequest(question));
}
