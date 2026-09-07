using System.Text.RegularExpressions;

namespace PrawoRAG.Domain.Privacy;

/// <summary>Kategoria wykrytej danej wrażliwej (do etykiety w ostrzeżeniu UI).</summary>
public enum SensitiveDataKind { Pesel, Nip, Regon, DowodOsobisty, Telefon, Email, RachunekBankowy }

/// <summary>
/// Deterministyczny detektor MOŻLIWYCH danych osobowych/wrażliwych w tekście pytania — na potrzeby
/// NIEBLOKUJĄCEGO ostrzeżenia w UI („opisz sprawę abstrakcyjnie, bez danych klienta"). To PODPOWIEDŹ,
/// nie gwarancja: wyłapuje wzorce STRUKTURALNE o wysokiej precyzji (z sumami kontrolnymi tam, gdzie
/// istnieją), świadomie POMIJA imiona/nazwiska (NER = fałszywe alarmy, zawodne przy polskiej odmianie).
///
/// Wysoka precyzja jest celowa: tekst prawny roi się od liczb (art. 415, sygn. „II CSK 123/45", lata,
/// ELI „DU/2026/473") — bez sum kontrolnych i granic tokenów detektor krzyczałby na wszystko. Czysta,
/// bez zależności, w pełni testowalna (wzór: <c>Retrieval/AcronymDetector</c>).
/// </summary>
public static class SensitiveDataDetector
{
    // E-mail: standardowy kształt local@domena.tld.
    private static readonly Regex EmailRe =
        new(@"[\p{L}0-9._%+\-]+@[\p{L}0-9.\-]+\.\p{L}{2,}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Telefon PL: WYMAGA prefiksu +48 albo grupowania 3-3-3 separatorami — NIE gołych 9 cyfr
    // (inaczej łapałby kwoty/sygnatury/lata). Dopasowuje „+48 123 456 789", „+48123456789",
    // „123-456-789", „123 456 789".
    private static readonly Regex PhoneRe =
        new(@"(?<![\d+])(?:\+48[\s-]?\d{3}[\s-]?\d{3}[\s-]?\d{3}|\d{3}[\s-]\d{3}[\s-]\d{3})(?![\d-])",
            RegexOptions.Compiled);

    // Rachunek/IBAN PL: prefiks PL + 26 cyfr (opcjonalnie w grupach), albo goły ciąg 26 cyfr.
    private static readonly Regex IbanRe =
        new(@"(?<![\w])PL[\s]?\d{2}(?:[\s]?\d{4}){6}(?![\d])|(?<!\d)\d{26}(?!\d)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Nr dowodu osobistego: 3 litery + 6 cyfr (ABC123456). Format sam w sobie bardzo specyficzny —
    // sumę kontrolną świadomie pomijamy (ryzyko błędnej implementacji > zysk; sygnatury mają spacje/„/").
    private static readonly Regex DowodRe =
        new(@"(?<![\p{L}\d])[A-Za-z]{3}\s?\d{6}(?![\p{L}\d])", RegexOptions.Compiled);

    // NIP zapisany z separatorami: 3-3-2-2 (odróżnia od telefonu 3-3-3). Waliduje sumę po odsianiu cyfr.
    private static readonly Regex NipSepRe =
        new(@"(?<!\d)\d{3}[-\s]\d{3}[-\s]\d{2}[-\s]\d{2}(?!\d)", RegexOptions.Compiled);

    // Samodzielny ciąg 9–14 cyfr → kandydat na PESEL(11)/NIP(10)/REGON(9|14) — rozstrzyga suma kontrolna.
    private static readonly Regex DigitRunRe = new(@"(?<!\d)\d{9,14}(?!\d)", RegexOptions.Compiled);

    /// <summary>Wykryte kategorie danych, bez duplikatów, w stałej kolejności. Pusto dla pustego/null.</summary>
    public static IReadOnlyList<SensitiveDataKind> Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var hits = new HashSet<SensitiveDataKind>();

        if (EmailRe.IsMatch(text)) hits.Add(SensitiveDataKind.Email);
        if (PhoneRe.IsMatch(text)) hits.Add(SensitiveDataKind.Telefon);
        if (IbanRe.IsMatch(text)) hits.Add(SensitiveDataKind.RachunekBankowy);
        if (DowodRe.IsMatch(text)) hits.Add(SensitiveDataKind.DowodOsobisty);

        foreach (Match m in NipSepRe.Matches(text))
            if (IsValidNip(DigitsOnly(m.Value))) hits.Add(SensitiveDataKind.Nip);

        foreach (Match m in DigitRunRe.Matches(text))
        {
            var d = m.Value;
            switch (d.Length)
            {
                case 11 when IsValidPesel(d): hits.Add(SensitiveDataKind.Pesel); break;
                case 10 when IsValidNip(d): hits.Add(SensitiveDataKind.Nip); break;
                case 9 when IsValidRegon9(d): hits.Add(SensitiveDataKind.Regon); break;
                case 14 when IsValidRegon14(d): hits.Add(SensitiveDataKind.Regon); break;
            }
        }

        // Stała kolejność (wg wartości enuma) — deterministyczny tekst tooltipa.
        return hits.OrderBy(k => (int)k).ToList();
    }

    /// <summary>Etykieta kategorii do ostrzeżenia w UI.</summary>
    public static string Label(SensitiveDataKind kind) => kind switch
    {
        SensitiveDataKind.Pesel => "PESEL",
        SensitiveDataKind.Nip => "NIP",
        SensitiveDataKind.Regon => "REGON",
        SensitiveDataKind.DowodOsobisty => "nr dowodu",
        SensitiveDataKind.Telefon => "nr telefonu",
        SensitiveDataKind.Email => "e-mail",
        SensitiveDataKind.RachunekBankowy => "nr rachunku",
        _ => kind.ToString(),
    };

    private static string DigitsOnly(string s) => new(s.Where(char.IsDigit).ToArray());

    private static int WeightedSum(string digits, int[] weights)
    {
        var sum = 0;
        for (var i = 0; i < weights.Length; i++) sum += (digits[i] - '0') * weights[i];
        return sum;
    }

    private static bool IsValidPesel(string d)
    {
        var control = (10 - WeightedSum(d, [1, 3, 7, 9, 1, 3, 7, 9, 1, 3]) % 10) % 10;
        return control == d[10] - '0';
    }

    private static bool IsValidNip(string d)
    {
        if (d.Length != 10) return false;
        var control = WeightedSum(d, [6, 5, 7, 2, 3, 4, 5, 6, 7]) % 11;
        return control != 10 && control == d[9] - '0';
    }

    private static bool IsValidRegon9(string d)
    {
        var control = WeightedSum(d, [8, 9, 2, 3, 4, 5, 6, 7]) % 11 % 10;
        return control == d[8] - '0';
    }

    private static bool IsValidRegon14(string d)
    {
        var control = WeightedSum(d, [2, 4, 8, 5, 0, 9, 7, 3, 6, 1, 2, 4, 8]) % 11 % 10;
        return control == d[13] - '0';
    }
}
