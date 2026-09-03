using Microsoft.Extensions.Logging;

namespace PrawoRAG.Tests.Fakes;

/// <summary>
/// <see cref="ILogger{T}"/> zbierający wpisy do listy — pozwala sprawdzić, że degradacja
/// (np. awaria rerankera) faktycznie ZOSTAWIA ŚLAD, zamiast dziać się po cichu.
/// </summary>
public sealed class CollectingLogger<T> : ILogger<T>
{
    public sealed record Entry(LogLevel Level, string Message, Exception? Exception);

    public List<Entry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add(new Entry(logLevel, formatter(state, exception), exception));
}
