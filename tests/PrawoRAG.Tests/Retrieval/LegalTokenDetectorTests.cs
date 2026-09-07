using PrawoRAG.Domain.Retrieval;

namespace PrawoRAG.Tests.Retrieval;

/// <summary>
/// T-LEGALTOK (Zadanie 6 planu ROU) — deterministyczny BEZPIECZNIK jednokierunkowy przed routerem.
///
/// Rola: jeżeli w wiadomości JEST odwołanie prawne, retrieval jest wymuszony niezależnie od tego, co
/// orzekł router (i nawet gdy router padł). Ten detektor NIE POTRAFI orzec, że szukać nie trzeba —
/// potrafi tylko wymusić szukanie. Dlatego nie da się go przeciążyć nowymi intencjami.
///
/// Asymetria, którą te testy pilnują: fałszywe TRUE kosztuje tylko czas (zbędny retrieval), fałszywe
/// FALSE oddaje decyzję routerowi (modelowi 6–11 B) — a to jedyne miejsce, gdzie pomyłka może dać
/// odpowiedź prawną bez źródeł. Dlatego przypadki graniczne rozstrzygamy na TRUE.
/// </summary>
public class LegalTokenDetectorTests
{
    [Theory] // Jawne odwolania - MUSZA wymusic retrieval.
    [InlineData("co z art. 5?")]                                  // krótkie, ale prawne
    [InlineData("art 415 kc")]                                    // bez kropek
    [InlineData("Czy artykuł 148 kodeksu karnego się stosuje?")]   // pełna odmiana
    [InlineData("a co z § 2?")]                                   // paragraf (typowy follow-up)
    [InlineData("III SA/Po 154/26")]                              // goła sygnatura akt
    [InlineData("co mówi wyrok II AKo 174/22 w tej sprawie?")]     // sygnatura w zdaniu
    [InlineData("Dz.U. 2025 poz. 1815")]                          // numer Dziennika Ustaw
    [InlineData("DU/2011/657")]                                   // ELI wprost
    [InlineData("ustawa o ochronie danych osobowych")]            // nazwa aktu
    [InlineData("ordynacja podatkowa")]                           // nazwa aktu (ordynacja)
    [InlineData("co reguluje KSeF?")]                             // akronim
    [InlineData("dzięki, a co z terminem z art. 300?")]            // podziękowanie + pytanie prawne
    [InlineData("Czy aplikant adwokacki może zastępować radcę prawnego na podstawie art. 77 ustawy o adwokaturze?")]
    public void Detects_legal_reference(string text) =>
        Assert.True(LegalTokenDetector.ContainsLegalReference(text));

    [Theory] // Brak odwolania - bezpiecznik milczy i decyzje podejmuje router (Zadanie 7).
    [InlineData("siema")]
    [InlineData("dzięki!")]
    [InlineData("co potrafisz?")]
    [InlineData("kim jesteś")]
    [InlineData("napisz coś o kotach")]
    [InlineData("")]
    [InlineData("   ")]
    public void Stays_silent_without_legal_reference(string text) =>
        Assert.False(LegalTokenDetector.ContainsLegalReference(text));

    [Fact] // null nie moze rzucic - detektor stoi na samym wejsciu tury.
    public void Null_is_safe() => Assert.False(LegalTokenDetector.ContainsLegalReference(null));

    [Fact] // Pytanie prawne BEZ zadnego tokenu (opisowe) - bezpiecznik go NIE zlapie i to jest OK:
           // to wlasnie przypadek, dla ktorego istnieje router. Test dokumentuje granice mechanizmu,
           // zeby nikt nie uznal bezpiecznika za kompletna ochrone.
    public void Descriptive_legal_question_is_not_detected_by_design()
    {
        Assert.False(LegalTokenDetector.ContainsLegalReference(
            "czy pracodawca może mnie zwolnić w czasie zwolnienia lekarskiego?"));
    }

    [Fact] // Sam fakt uzycia slowa o wydzwieku prawnym NIE wystarcza - inaczej detektor lapalby
           // wszystko i router stalby sie martwym kodem (a jego pomiar trafnosci - bezwartosciowy).
    public void Bare_legal_sounding_word_is_not_a_reference()
    {
        Assert.False(LegalTokenDetector.ContainsLegalReference("umowa"));
        Assert.False(LegalTokenDetector.ContainsLegalReference("prawo do obrony"));
    }
}
