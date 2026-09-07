using System.Text.RegularExpressions;

namespace PrawoRAG.Domain.Documents;

/// <summary>
/// Detektor chunków zdegenerowanych (JAK-0/1, raport odmów 2026-07-18/19): fragmenty bez treści
/// informacyjnej mają anomalnie „lepkie" embeddingi (ten sam mechanizm co REGULATION) i wypychają
/// realne przepisy ze źródeł. Dwie klasy, obie zmierzone na realnych odmowach:
/// 1. Placeholdery tekstów jednolitych: „Art. 9-21. (pominięte)", „(uchylony)" — Case 4/5
///    (zmierzone 1056 chunków `(pominięt*)`); serwują pustkę jako źródło.
/// 2. Szum anonimizacyjny SAOS: „(...) roku, (...) z dnia (...)" ×10, „kontrola operacyjna (...)
///    pod kryptonimem (...)" — Case 5; krótkie, silnie powtarzalne frazy zdominowane znacznikiem
///    anonimizacji tworzą sztucznie centralny klaster w przestrzeni wektorowej.
/// Czysty i deterministyczny — używany przez sanitizer (--sanitize-chunks) na istniejącej bazie
/// i docelowo przez chunker (JAK-2), żeby reprocessing nie odtwarzał śmieci.
/// </summary>
public static class ChunkDegeneracy
{
    /// <summary>Znacznik anonimizacji SAOS: „(...)", także z wielokropkiem typograficznym „(…)".</summary>
    private static readonly Regex AnonMarkRe = new(@"\(\s*(\.{3}|…)\s*\)", RegexOptions.Compiled);

    /// <summary>Słowo znaczące: ≥3 litery (jak MinSubstantiveWords w chunkerze).</summary>
    private static readonly Regex SubstantiveWordRe =
        new(@"[A-Za-zĄĆĘŁŃÓŚŹŻąćęłńóśźż]{3,}", RegexOptions.Compiled);

    /// <summary>Słowa, które NIE liczą się jako treść w teście placeholdera: sam szkielet
    /// „Art. N. (pominięty)"/„(uchylony)" w dowolnej odmianie i liczbie.</summary>
    private static readonly HashSet<string> PlaceholderVocabulary = new(StringComparer.OrdinalIgnoreCase)
    {
        "art", "artykuł", "artykuły", "pominięty", "pominięta", "pominięte", "pominięci",
        "uchylony", "uchylona", "uchylone", "uchylenie", "skreślony", "skreślona", "skreślone",
    };

    public static bool IsDegenerate(string text) =>
        IsOmittedPlaceholder(text) || IsAnonymizationNoise(text);

    /// <summary>Placeholder uchylenia/pominięcia: po odjęciu szkieletu („art", numery, interpunkcja)
    /// nie zostaje ŻADNE słowo treści — cały chunk to informacja „tu niczego nie ma".</summary>
    public static bool IsOmittedPlaceholder(string text)
    {
        var words = SubstantiveWordRe.Matches(text).Select(m => m.Value).ToList();
        return words.Count > 0 && words.All(w => PlaceholderVocabulary.Contains(w));
    }

    /// <summary>Szum anonimizacyjny: znaczniki „(...)" dominują treść — ≥3 znaczniki i co najmniej
    /// jeden znacznik na każde 2 słowa znaczące. Kalibracja na wzorcach z Case 5: „(...) roku,
    /// (...) z dnia (...)" ×N (2 słowa/3 znaczniki na powtórzenie) kwalifikuje się; narracja
    /// z kilkoma zanonimizowanymi nazwiskami w setkach słów — nie.</summary>
    public static bool IsAnonymizationNoise(string text)
    {
        var anonMarks = AnonMarkRe.Matches(text).Count;
        if (anonMarks < 3) return false;
        var substantive = SubstantiveWordRe.Matches(text).Count;
        return anonMarks * 2 >= substantive;
    }
}
