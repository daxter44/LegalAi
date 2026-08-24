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
    /// Zwraca zapytanie przełożone na język przepisów albo <c>null</c>, gdy: model padł/timeout,
    /// wyjście jest puste, ALBO wyjście jest równoważne wejściu (druga runda byłaby wtedy
    /// deterministycznym powtórzeniem pierwszej — czysta strata ~40 s bez żadnej szansy na inny wynik).
    /// </summary>
    Task<string?> ReformulateAsync(string question, CancellationToken ct);
}
