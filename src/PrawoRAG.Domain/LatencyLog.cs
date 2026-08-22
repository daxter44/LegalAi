namespace PrawoRAG.Domain;

/// <summary>
/// Diagnostyka latencji per etap odpowiedzi czatu (2026-08-23) — user zgłosił 4-5 minut na jedno
/// pytanie i brak jakiejkolwiek instrumentacji w kodzie, żeby wskazać który etap przyspieszać
/// (embedding? tor gęsty/rzadki? reranker? most cytowań? augmenter nowel? sam LLM?). Gated env-varem
/// (wzorem `PRAWORAG_LOG_FOLLOWUP` z Zadania 6, `docs/PLAN-ODSIEW-SZUMU-RETRIEVALU.md`) — domyślnie
/// WYŁĄCZONE (zero narzutu, zero zmiany zachowania), włączane na czas jednego pomiaru.
/// </summary>
public static class LatencyLog
{
    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("PRAWORAG_LOG_TIMING") is { Length: > 0 };

    /// <summary>Mierzy i loguje czas asynchronicznej operacji zwracającej wartość. Gdy diagnostyka
    /// wyłączona — bezpośrednie wywołanie, bez tworzenia Stopwatch.</summary>
    public static async Task<T> TimeAsync<T>(string stage, Func<Task<T>> action)
    {
        if (!Enabled) return await action();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { return await action(); }
        finally { Console.WriteLine($"[timing] {stage} = {sw.ElapsedMilliseconds} ms"); }
    }

    /// <summary>Jak <see cref="TimeAsync{T}"/>, dla operacji bez wartości zwrotnej (np. augmentacja
    /// wołana z odrzuceniem best-effort — caller sam owija w try/catch, tu tylko czas).</summary>
    public static async Task TimeAsync(string stage, Func<Task> action)
    {
        if (!Enabled) { await action(); return; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { await action(); }
        finally { Console.WriteLine($"[timing] {stage} = {sw.ElapsedMilliseconds} ms"); }
    }

    /// <summary>Ręczny wpis, gdy stoper trzeba trzymać przez wiele kroków w jednej metodzie (np.
    /// czas do pierwszego tokenu LLM vs czas całego strumienia).</summary>
    public static void Mark(string stage, long ms)
    {
        if (Enabled) Console.WriteLine($"[timing] {stage} = {ms} ms");
    }

    /// <summary>Czy diagnostyka jest włączona — dla callerów, którym samo <see cref="TimeAsync{T}"/>
    /// nie wystarcza (np. Stopwatch obejmujący nie-async fragment kodu).</summary>
    public static bool IsEnabled => Enabled;
}
