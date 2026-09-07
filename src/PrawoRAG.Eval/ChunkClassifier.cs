using System.Text.RegularExpressions;
using PrawoRAG.Domain;

namespace PrawoRAG.Eval;

/// <summary>Rodzaj chunka w rankingu retrievalu — pojedyncza, priorytetowa etykieta (patrz
/// <see cref="ChunkClassifier.Classify"/>). Kolejność w enumie = kolejność priorytetu klasyfikacji.</summary>
public enum ChunkKind
{
    /// <summary>Orzeczenie (SAOS/NSA/WSA) — DocType ≠ „act". Właściwa treść dla pytań o linię orzeczniczą.</summary>
    Judgment,

    /// <summary>Jednostka aktu oznaczona „(uchylony)"/„(pominięty)" — martwa treść, nie powinna
    /// konkurować z obowiązującym brzmieniem (kandydat do odsiewu w retrievalu).</summary>
    RepealedOrOmitted,

    /// <summary>Jednostka-wariant „(wariant N/M)" — to samo Art./§/pkt w kilku brzmieniach czasowych,
    /// near-duplikat który POKONUJE dedup po dokładnym tekście (etykieta różni się sufiksem). Cel CIT-3.</summary>
    AmendmentVariant,

    /// <summary>Jednostka noweli (tytuł „… o zmianie ustawy …"), nie-wariant — treść zmieniająca inny
    /// akt, leksykalnie bliska aktowi bazowemu, konkuruje z nim o miejsca w puli. Cel CIT-3.</summary>
    AmendmentAct,

    /// <summary>Cienki chunk-wyliczenie aktu BAZOWEGO: pojedynczy punkt listy („1) zapłaty,") o małej
    /// liczbie tokenów. Kandydat do weryfikacji degeneracji (CIT-4).</summary>
    ThinEnumeration,

    /// <summary>Zwykła jednostka aktu bazowego — klasa DOCELOWA (to tu żyją definicje/normy rządzące).</summary>
    BaseAct,
}

/// <summary>Obserwowalne cechy chunka wystarczające do klasyfikacji — bez zależności od DB/EF, żeby
/// klasyfikator był czystą, testowalną funkcją (sonda mapuje wiersz na to, testy podają wprost).</summary>
public readonly record struct ChunkFacts(string DocType, string Title, string? Section, string Text, int TokenCount);

/// <summary>Wynik klasyfikacji: pojedyncza etykieta priorytetowa (<see cref="Kind"/>) + ortogonalne
/// flagi surowe. Flagi pozwalają zliczyć nakładające się kategorie (np. nowela KTÓRA JEST wariantem)
/// tak, jak liczono ręcznie w PRZYPADEK-BUDOWLA-BUDYNEK-UPOL — bez gubienia informacji przez priorytet.</summary>
public readonly record struct ChunkClass(
    ChunkKind Kind, bool IsAct, bool IsAmendmentAct, bool IsVariant,
    bool IsRepealedOrOmitted, bool IsEnumerationPoint, bool IsThin);

/// <summary>
/// Klasyfikator pozycji rankingu (instrument CIT-2, nie ścieżka produkcyjna). Odpowiada na pytanie
/// „co realnie zajmuje fused top-N" bez oceniania okiem: czy to śmieć strukturalny (wariant/uchylony/
/// cienkie wyliczenie), czy trafna treść, która i tak przegrywa. Klasyfikacja z JAWNYCH sygnałów
/// nadawanych w ingestii — sufiks „(wariant N/M)" i „(uchylony)"/„(pominięty)" w etykiecie jednostki
/// (<c>ActNormalizer.DisambiguateDuplicateUnits</c>), tytuł „… o zmianie …" (nowela), kształt punktu
/// wyliczenia w treści. CZYSTA funkcja: te same wejścia → ten sam wynik, zero I/O.
/// </summary>
public static class ChunkClassifier
{
    /// <summary>Sufiks nadawany duplikatom jednostek czasowych w <c>ActNormalizer</c>.</summary>
    private static readonly Regex VariantRe = new(@"\(wariant\s+\d+/\d+\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    /// <summary>„(uchylony)"/„(uchylona)"/„(pominięty)"/„(pominięta)" — jednostka bez żywej treści.</summary>
    private static readonly Regex RepealedRe = new(@"\((?:uchylon|pomini[ęe]t)\w*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    /// <summary>Tytuł aktu nowelizującego: „… o zmianie ustawy …".</summary>
    private static readonly Regex AmendmentTitleRe = new(@"\bo\s+zmianie\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    /// <summary>Początek treści = punkt wyliczenia: „1)", „12)", „1a)", „a)".</summary>
    private static readonly Regex EnumStartRe = new(@"^(?:\d{1,3}[a-z]?|[a-z])\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Próg „cienkiego" chunka (tokeny). Punkt wyliczenia bywa kilka–kilkanaście tokenów;
    /// docelowa definicja art. 1a u.p.o.l. miała 436 tok. — 40 pewnie rozdziela jedno od drugiego.</summary>
    public const int ThinTokenThreshold = 40;

    /// <summary>Priorytet etykiety (gdy cech jest kilka): martwa treść → wariant → nowela → cienkie
    /// wyliczenie aktu bazowego → baza. Nowela WYGRYWA z „cienkim wyliczeniem", więc cienki punkt noweli
    /// liczy się jako nowela (dźwignia CIT-3), a klasa <see cref="ChunkKind.ThinEnumeration"/> obejmuje
    /// tylko cienkie punkty aktów BAZOWYCH — dokładnie te, które rozważa CIT-4. Nakładki widać we flagach.</summary>
    public static ChunkClass Classify(in ChunkFacts f)
    {
        var isAct = string.Equals(f.DocType, DocTypes.Act, StringComparison.OrdinalIgnoreCase);
        var section = f.Section ?? "";
        var isVariant = VariantRe.IsMatch(section);
        var isRepealed = RepealedRe.IsMatch(section);
        var isAmendment = isAct && AmendmentTitleRe.IsMatch(f.Title);
        var isEnum = EnumStartRe.IsMatch(Body(f.Text));
        var isThin = f.TokenCount > 0 && f.TokenCount <= ThinTokenThreshold;

        var kind =
            !isAct ? ChunkKind.Judgment
            : isRepealed ? ChunkKind.RepealedOrOmitted
            : isVariant ? ChunkKind.AmendmentVariant
            : isAmendment ? ChunkKind.AmendmentAct
            : isEnum && isThin ? ChunkKind.ThinEnumeration
            : ChunkKind.BaseAct;

        return new ChunkClass(kind, isAct, isAmendment, isVariant, isRepealed, isEnum, isThin);
    }

    /// <summary>Treść bez nagłówka kontekstowego: chunk aktu ma nagłówek („Art. N § M …") w pierwszej
    /// linii, właściwa treść jest po pierwszym „\n". Bez „\n" — cały tekst. Trim, żeby wzorzec punktu
    /// łapał od pierwszego znaku.</summary>
    private static string Body(string text)
    {
        var nl = text.IndexOf('\n');
        return (nl >= 0 ? text[(nl + 1)..] : text).TrimStart();
    }
}
