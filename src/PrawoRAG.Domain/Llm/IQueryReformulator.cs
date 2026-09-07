namespace PrawoRAG.Domain.Llm;

/// <summary>
/// Przeformułowanie zapytania na terminologię ustawową (Zadanie 11 planu ROU) — dla DRUGIEJ rundy
/// retrievalu, gdy pierwsza nie domknęła pytania.
///
/// W co to celuje: <c>docs/DIAGNOZA-BM25-POLSKI-2026-08-15.md</c> §9 orzeka o przypadkach
/// <c>uodo-107</c>/<c>uodo-60</c>, że to **niedopasowanie terminologii prawnej — synonim, nie forma
/// słowa** — i że wymaga „wnioskowania LLM nad dobrze dobranym kontekstem lub słownika synonimów
/// prawnych, nie lematyzacji". To jest ten mechanizm. Atakuje przypadki ZMIERZONE i nazwane,
/// nie hipotetyczne.
///
/// KONTRAKT: zwraca <c>null</c> zamiast rzucać. Null znaczy „nie ma sensownego wariantu" i prowadzi
/// do dzisiejszego zachowania (odmowa), nigdy do wyjątku na ścieżce czatu.
/// </summary>
public interface IQueryReformulator
{
    /// <summary>
    /// Zwraca SAMODZIELNE zapytanie przełożone na język przepisów albo <c>null</c>, gdy: model
    /// padł/timeout, wyjście jest puste, ALBO wyjście jest równoważne wejściu (druga runda byłaby
    /// wtedy deterministycznym powtórzeniem pierwszej — czysta strata ~40 s bez żadnej szansy na
    /// inny wynik).
    /// </summary>
    /// <param name="history">
    /// Poprzednie tury rozmowy. NIE jest opcjonalna i NIE ma przeciążki bez niej — na follow-upie
    /// („a co z § 2?") samo <paramref name="question"/> nie niesie treści, więc bez historii ten
    /// mechanizm przekładał na terminologię ustawową tekst bez tematu. Dokładnie w tej klasie tur
    /// odmowy są najczęstsze, czyli tam, gdzie druga runda ma najwięcej do uratowania.
    /// Pusta lista = pytanie otwierające rozmowę (zachowanie jak dotąd).
    ///
    /// KONSEKWENCJA DLA WOŁAJĄCEGO: wynik jest już samodzielny, więc NIE wolno go ponownie sklejać
    /// z historią w <see cref="PrawoRAG.Domain.Retrieval.FollowUpSelector"/> — patrz
    /// <see cref="PrawoRAG.Domain.Retrieval.GapClosingRetrieval"/>.
    /// </param>
    Task<string?> ReformulateAsync(string question, IReadOnlyList<ChatTurn> history, CancellationToken ct);
}
