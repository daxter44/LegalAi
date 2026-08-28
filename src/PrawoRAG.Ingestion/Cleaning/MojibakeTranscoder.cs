using System.Text;
using System.Text.RegularExpressions;

namespace PrawoRAG.Ingestion.Cleaning;

/// <summary>
/// Naprawa mojibake ze starych PDF-ów Dz.U. (~2000–2009): fonty tych plików mają treść w bajtach
/// Mac-CE (CP 10029), a uszkodzona/brakująca mapa ToUnicode każe je czytać jako MacRoman (CP 10000) —
/// stąd „Je˝eli"→„Jeżeli", „uchwa∏y"→„uchwały", „zadaƒ"→„zadań". Mapa poniżej to komplet polskich
/// liter wyprowadzony z obu kodowań (bajt = Encoding(10029).GetBytes(litera), objaw =
/// Encoding(10000).GetString(bajt)); „ó"/„Ó" mają w obu kodowaniach ten sam bajt, więc wyświetlały
/// się poprawnie i mapy nie potrzebują.
///
/// Transkodyzację stosujemy WYŁĄCZNIE do tekstu z sygnaturą <see cref="LooksAffected"/> — znaki
/// mapy bywają legalne w obcych słowach (à, ç, è), ale ∏/Ê/˝/ƒ w naturalnym polskim tekście prawnym
/// nie występują, więc ich obecność jednoznacznie identyfikuje uszkodzony dokument.
/// </summary>
public static class MojibakeTranscoder
{
    // Znaki-sygnatury: występują tylko w tekście uszkodzonym (odpowiedniki ł/ś/ż/ń — najczęstszych
    // polskich znaków diakrytycznych, więc każdy realnie uszkodzony dokument zawiera choć jeden).
    private static readonly Regex Signature = new(@"[∏Ê˝ƒ]", RegexOptions.Compiled);

    // MacRoman(bajt) → Mac-CE(bajt) dla polskich liter. Kolejność: małe, potem wielkie.
    private static readonly Dictionary<char, char> Map = new()
    {
        ['à'] = 'ą', ['ç'] = 'ć', ['´'] = 'ę', ['∏'] = 'ł', ['ƒ'] = 'ń',
        ['Ê'] = 'ś', ['ê'] = 'ź', ['˝'] = 'ż',
        ['Ñ'] = 'Ą', ['å'] = 'Ć', ['¢'] = 'Ę', ['¸'] = 'Ł', ['¡'] = 'Ń',
        ['Â'] = 'Ś', ['è'] = 'Ź', ['˚'] = 'Ż',
    };

    // Łamanie wyrazu z przenoszeniem („po-winny", „zatrzy-mywania") — ekstrakcja PDF zgubiła
    // granice linii, więc dywiz stoi w środku słowa. Sklejamy tylko litera-litera (liczby i
    // zakresy „10-15" nietknięte). Świadomy kompromis: złoży też rzadkie legalne złożenia
    // („administracyjno-prawny"→„administracyjnoprawny") — w dokumentach i tak uszkodzonych
    // zysk (odtworzone słowa) przeważa nad stratą.
    private static readonly Regex Hyphenation =
        new(@"(?<=[a-ząćęłńóśźż]{2})-(?=[a-ząćęłńóśźż]{2})", RegexOptions.Compiled);

    /// <summary>Czy tekst nosi sygnaturę uszkodzenia (i wymaga <see cref="Fix"/>).</summary>
    public static bool LooksAffected(string text) => Signature.IsMatch(text);

    /// <summary>Transkodyzacja + sklejenie przeniesień. Wołać tylko gdy <see cref="LooksAffected"/>.</summary>
    public static string Fix(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
            sb.Append(Map.TryGetValue(ch, out var fixedCh) ? fixedCh : ch);
        return Hyphenation.Replace(sb.ToString(), "");
    }

    /// <summary>Napraw, gdy tekst nosi sygnaturę; w przeciwnym razie zwróć bez zmian.</summary>
    public static string FixIfAffected(string text) => LooksAffected(text) ? Fix(text) : text;
}
