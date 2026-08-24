namespace PrawoRAG.Domain.Llm;

/// <summary>
/// Orzeczenie routera intencji (Zadanie 7 planu ROU).
/// </summary>
/// <param name="PotrzebnePrzepisy">
/// Czy do odpowiedzi potrzebne są przepisy/orzeczenia z bazy. <c>false</c> uruchamia ścieżkę bez
/// retrievalu (small-talk), jawnie oznaczoną w UI jako nieopartą na źródłach.
/// </param>
/// <param name="Zapytanie">
/// Zapytanie do bazy proponowane przez router (poprawione literówki, rozwinięte potoczne
/// sformułowanie). W Fazie 2 jest wyłącznie LOGOWANE — podmiana wejścia retrievalu to zmiana
/// jakościowa wymagająca osobnej weryfikacji, a nie skutek uboczny routingu.
/// </param>
/// <param name="Uzasadnienie">Krótkie uzasadnienie — do logu i diagnostyki, nie do UI.</param>
public sealed record RouteDecision(bool PotrzebnePrzepisy, string? Zapytanie, string Uzasadnienie)
{
    /// <summary>
    /// Domyślne, BEZPIECZNE orzeczenie: idziemy do bazy. Używane przy każdej awarii routera
    /// (timeout, wyjątek, nieparsowalne wyjście) — decyzja przekrojowa 3 planu ROU: fail-safe
    /// zawsze w stronę retrievalu, nigdy „pomiń bazę".
    /// </summary>
    public static RouteDecision Retrieval(string reason) => new(true, null, reason);
}

/// <summary>
/// Router intencji — rozstrzyga PRZED retrievalem, czy pytanie w ogóle wymaga sięgania do bazy
/// przepisów i orzeczeń. Powód: dziś każda wiadomość, także „siema", przechodzi pełny pipeline po
/// 7,4 mln fragmentów i płaci ~85 s (pomiar <c>PRAWORAG_LOG_TIMING</c>).
///
/// KONTRAKT IMPLEMENTACJI (nienegocjowalny — na nim stoi rdzeń wartości produktu):
/// implementacja NIE MOŻE rzucić wyjątku ani zwrócić null. Każda awaria, timeout, puste albo
/// nieparsowalne wyjście modelu kończy się <see cref="RouteDecision.Retrieval"/>. Powód jest
/// asymetryczny: small-talk wpuszczony do retrievalu kosztuje tylko czas, ale pytanie prawne
/// uznane za small-talk daje odpowiedź BEZ źródeł.
///
/// Router jest jedynie JEDNĄ z dwóch linii: przed nim stoi deterministyczny
/// <see cref="PrawoRAG.Domain.Retrieval.LegalTokenDetector"/>, który wymusza retrieval dla jawnych
/// odwołań prawnych — i wtedy router nie jest w ogóle wołany (oszczędzone wywołanie modelu).
/// </summary>
public interface IIntentRouter
{
    Task<RouteDecision> RouteAsync(string question, IReadOnlyList<ChatTurn> history, CancellationToken ct);
}
