namespace PrawoRAG.Domain;

/// <summary>
/// <see cref="IProgress{T}"/>, który wywołuje handler SYNCHRONICZNIE, na wątku raportującym.
///
/// Dlaczego nie <see cref="Progress{T}"/> z BCL: on celowo dyspozycjonuje callback przez
/// <c>SynchronizationContext.Post</c> (albo pulę wątków), bo jego zadaniem jest przeskoczyć na wątek
/// UI. Skutki uboczne, które nas dyskwalifikują — oba złapane testem T-STAGE-CHAT: (1) raporty
/// docierają PÓŹNIEJ niż praca, którą opisują (etapy retrievalu pojawiały się po „Piszę odpowiedź…"),
/// (2) ich wzajemna KOLEJNOŚĆ nie jest gwarantowana, więc UI pokazywałoby etapy pomieszane.
///
/// Tutaj marshalling na właściwy wątek robi warstwa wyżej (kanał zdarzeń w <c>ChatService</c>,
/// pompa SSE w <c>/api/chat</c>) — więc <see cref="IProgress{T}"/> ma być cienki i natychmiastowy.
/// Handler MUSI być szybki i nierzucający: leci w środku pipeline'u retrievalu.
/// </summary>
public sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
