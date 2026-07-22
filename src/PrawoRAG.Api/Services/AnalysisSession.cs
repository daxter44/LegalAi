using PrawoRAG.Llm.Grounding;

namespace PrawoRAG.Api.Services;

/// <summary>Przełączniki trybu „Analiza dokumentów" (spike SPK). <see cref="Enabled"/>=false (domyślnie)
/// chowa stronę /analiza. <see cref="MaxParallelism"/> — limit RÓWNOCZESNYCH wywołań LLM w fazie map:
/// lokalny Bielik na jednej karcie i tak generuje sekwencyjnie, więcej równoległości = dłuższy łączny
/// czas i wysycenie VRAM; dla API cloud można podnieść.</summary>
public sealed class AnalysisOptions
{
    public const string SectionName = "Analysis";

    public bool Enabled { get; set; }
    public int MaxParallelism { get; set; } = 2;
    public int SessionTtlMinutes { get; set; } = 60;

    /// <summary>Twardy limit jednostek analizy na dokument (koszt: każda jednostka = wywołanie LLM).
    /// Nadmiar jest ucinany z jawną flagą <see cref="AnalysisSession.UnitsTruncated"/> — nigdy po cichu.</summary>
    public int MaxUnits { get; set; } = 40;
}

/// <summary><see cref="Interrupted"/> = anulowana przez użytkownika ALBO ucięta restartem procesu —
/// częściowy raport pozostaje czytelny (w odróżnieniu od <see cref="Failed"/> = awaria całości).</summary>
public enum AnalysisStatus { Preparing, Analyzing, Summarizing, Done, Failed, Interrupted }

/// <summary>Werdykt analizy jednej jednostki — parsowany z pierwszej linii odpowiedzi map-prompta
/// (albo nadany wprost: abstynencja → <see cref="NoSources"/>, wyjątek → <see cref="Error"/>).</summary>
public enum UnitVerdict { Unknown, Ok, Risk, NoSources, Error }

/// <summary>Wynik analizy JEDNEJ jednostki dokumentu (faza map). <see cref="Sources"/> przeniesione
/// strukturalnie z retrievalu tej jednostki — cytaty [n] w <see cref="Answer"/> odnoszą się do NICH
/// (numeracja per jednostka, nie per dokument).</summary>
public sealed record UnitAnalysis(
    int Index,
    string Heading,
    UnitVerdict Verdict,
    string? Answer,
    IReadOnlyList<ChatSource> Sources,
    CitationCheck? Check = null,
    string? Error = null);

/// <summary>
/// Stan jednej długiej analizy dokumentu (SPK-2) — żyje WYŁĄCZNIE w pamięci procesu
/// (<see cref="AnalysisSessionStore"/>): treść załącznika nigdy nie dotyka dysku ani bazy (tajemnica
/// zawodowa — decyzja #1 planu DOC). Id sesji jest biletem do podglądu postępu i do dopytań; restart
/// procesu lub TTL = sesja znika (komunikowane w UI, spójne z filozofią zero-persistence).
/// Thread-safe: mutacje pod lockiem, odczyt przez niemutowalny <see cref="Snapshot"/>.
/// </summary>
public sealed class AnalysisSession
{
    private readonly object _lock = new();
    private readonly UnitAnalysis?[] _results;
    private readonly CancellationTokenSource _cts = new();
    private AnalysisStatus _status = AnalysisStatus.Preparing;
    private int _completed;
    private string? _summary;
    private string? _error;

    /// <summary>Embeddingi jednostek (routing dopytań, SPK-6) — ustawiane raz w fazie przygotowania;
    /// null = embedding się nie powiódł (dopytania degradują się do trybu przekrojowego).</summary>
    private IReadOnlyList<float[]>? _unitEmbeddings;

    private readonly TimeProvider _time;

    public AnalysisSession(string userId, string fileName, int pageCount, string prompt, IReadOnlyList<DocUnit> units, bool unitsTruncated, TimeProvider time)
    {
        _time = time;
        UserId = userId;
        FileName = fileName;
        PageCount = pageCount;
        Prompt = prompt;
        Units = units;
        UnitsTruncated = unitsTruncated;
        CreatedAt = time.GetUtcNow();
        LastTouched = CreatedAt;
        _results = new UnitAnalysis?[units.Count];
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    /// <summary>Właściciel sesji — <see cref="AnalysisSessionStore.TryGet"/> odmawia dostępu przy
    /// niezgodności (id sesji NIE jest sekretem: pokazujemy go w UI, więc sam Guid nie może być
    /// biletem do cudzego dokumentu).</summary>
    public string UserId { get; }

    /// <summary>Token anulowania analizy — przekazywany do runnera; <see cref="Cancel"/> z UI
    /// (albo sweep TTL store'a) przerywa jednostki w locie. Ukończone wyniki zostają.</summary>
    public CancellationToken Token => _cts.Token;

    public void Cancel()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { /* wyścig ze sweepem — nieistotny */ }
    }

    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastTouched { get; private set; }
    public string FileName { get; }
    public int PageCount { get; }

    /// <summary>Intencja użytkownika (prompt) — doklejana do map-prompta każdej jednostki.</summary>
    public string Prompt { get; }

    public IReadOnlyList<DocUnit> Units { get; }
    public bool UnitsTruncated { get; }

    /// <summary>Sygnał zmiany stanu (postęp/wynik) — UI podpina odświeżenie. Wywoływany POZA lockiem.</summary>
    public event Action? Changed;

    public void Touch(DateTimeOffset now)
    {
        lock (_lock) LastTouched = now;
    }

    public bool IsExpired(DateTimeOffset now, TimeSpan ttl)
    {
        lock (_lock) return now - LastTouched > ttl;
    }

    public void SetStatus(AnalysisStatus status)
    {
        lock (_lock) _status = status;
        Changed?.Invoke();
    }

    public void SetUnitEmbeddings(IReadOnlyList<float[]> embeddings)
    {
        lock (_lock) _unitEmbeddings = embeddings;
    }

    public IReadOnlyList<float[]>? UnitEmbeddings
    {
        get { lock (_lock) return _unitEmbeddings; }
    }

    /// <summary>Zapis wyniku jednostki (faza map, wołane współbieżnie). Index = DocUnit.Index (1-based).
    /// Odświeża też <see cref="LastTouched"/> — analiza W TOKU sama przedłuża sobie TTL (bez tego
    /// sweep store'a mógłby ubić długą analizę, której nikt nie ogląda).</summary>
    public void SetUnitResult(UnitAnalysis result)
    {
        lock (_lock)
        {
            if (_results[result.Index - 1] is null) _completed++;
            _results[result.Index - 1] = result;
            LastTouched = _time.GetUtcNow();
        }
        Changed?.Invoke();
    }

    /// <summary>Indeksy jednostek z werdyktem BŁĄD — kandydaci do ponowienia (AN-4).</summary>
    public IReadOnlyList<int> ErrorUnitIndexes()
    {
        lock (_lock)
            return _results
                .Where(r => r is { Verdict: UnitVerdict.Error })
                .Select(r => r!.Index)
                .ToList();
    }

    /// <summary>Cofa jednostkę do stanu „w kolejce" przed ponowieniem: wynik znika, licznik spada,
    /// status wraca do Analyzing (UI pokazuje postęp jak przy pierwszym przebiegu).</summary>
    public void MarkUnitPending(int index)
    {
        lock (_lock)
        {
            if (_results[index - 1] is null) return;
            _results[index - 1] = null;
            _completed--;
            _status = AnalysisStatus.Analyzing;
        }
        Changed?.Invoke();
    }

    public void Complete(string? summary)
    {
        lock (_lock) { _summary = summary; _status = AnalysisStatus.Done; }
        Changed?.Invoke();
    }

    public void Fail(string error)
    {
        lock (_lock) { _error = error; _status = AnalysisStatus.Failed; }
        Changed?.Invoke();
    }

    /// <summary>Spójny, niemutowalny obraz stanu do renderu (wyniki w kolejności dokumentu; null =
    /// jednostka jeszcze w toku).</summary>
    public AnalysisSnapshot Snapshot()
    {
        lock (_lock)
            return new AnalysisSnapshot(
                Id, FileName, PageCount, Prompt, _status, Units, UnitsTruncated,
                [.. _results], _completed, _summary, _error);
    }
}

public sealed record AnalysisSnapshot(
    Guid Id,
    string FileName,
    int PageCount,
    string Prompt,
    AnalysisStatus Status,
    IReadOnlyList<DocUnit> Units,
    bool UnitsTruncated,
    IReadOnlyList<UnitAnalysis?> Results,
    int Completed,
    string? Summary,
    string? Error)
{
    public int Total => Units.Count;
}
