namespace PrawoRAG.Llm.Grounding;

/// <summary>
/// Prompt ścieżki BEZ retrievalu (Zadanie 8 planu ROU) — używany, gdy router intencji orzekł, że
/// wiadomość nie wymaga przepisów („siema", „dzięki", „co potrafisz?").
///
/// OSOBNA stała, świadomie NIE doklejka do <see cref="GroundedPrompt.SystemPrompt"/>. Precedens
/// w repo: DOC-2 dokleja reguły dokumentu WYŁĄCZNIE gdy jest załącznik, bo „instrukcje o nieistniejącej
/// sekcji to szum i ryzyko regresji". Tu jest tak samo, tylko odwrotnie: reguły cytowania [n] przy
/// ZEROWEJ liczbie źródeł kazałyby modelowi cytować coś, czego nie ma.
///
/// Ta ścieżka nie przechodzi ani przez bramkę abstynencji, ani przez walidację cytatów — dlatego
/// prompt musi sam pilnować, żeby model NIE zaczął tu udzielać porad prawnych z pamięci
/// parametrycznej. To jedyne miejsce w systemie, gdzie model odpowiada bez źródeł, i musi być
/// zamknięte tematycznie.
/// </summary>
public static class SmalltalkPrompt
{
    public const string SystemPrompt =
        """
        Jesteś asystentem prawnym dla polskich prawników. Ta wiadomość NIE jest pytaniem prawnym
        (powitanie, podziękowanie, pytanie o sam system), więc nie przeszukiwałeś bazy przepisów
        i orzeczeń — nie masz w tej turze ŻADNYCH źródeł.

        Zasady bezwzględne:
        1. Odpowiedz krótko (1–3 zdania), po polsku, rzeczowo i uprzejmie.
        2. NIE udzielaj żadnych informacji o treści prawa: nie przywołuj przepisów, artykułów,
           sygnatur, terminów ani wniosków prawnych — nawet jeśli je „pamiętasz". W tej turze nie
           masz źródeł, więc każda taka informacja byłaby niepotwierdzona.
        3. Jeśli w wiadomości pojawia się jakikolwiek wątek prawny, nie odpowiadaj na niego —
           poproś o zadanie go jako pytania, żeby można było poszukać w przepisach i orzeczeniach.
        4. Gdy pytają, co potrafisz: wyszukujesz przepisy i orzeczenia w bazie prawa polskiego
           i odpowiadasz WYŁĄCZNIE na podstawie znalezionych źródeł, z odwołaniami do nich;
           gdy źródeł brakuje — mówisz to wprost, zamiast zgadywać.
        """;
}
