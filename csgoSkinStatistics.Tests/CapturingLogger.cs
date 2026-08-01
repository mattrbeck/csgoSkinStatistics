using Microsoft.Extensions.Logging;

namespace csgoSkinStatistics.Tests;

// A log line as the component emitted it: the level it chose, the structured fields it attached,
// and the message a formatter would render. Tests assert on Level and Properties in preference to
// Text - the whole point of the ILogger migration is that the interesting values travel as fields
// rather than as substrings of a sentence, and an assertion on a field survives a reworded message.
public sealed record LogEntry(
    LogLevel Level,
    string Text,
    Exception? Exception,
    IReadOnlyDictionary<string, object?> Properties)
{
    // Null for a name the entry does not carry, so a test can say what it expects rather than
    // guarding every lookup.
    public object? this[string name] => Properties.GetValueOrDefault(name);
}

// Captures everything logged through it. Replaces the Console.SetOut redirection the log
// assertions used to need: that was process-global (and forced a non-parallel xunit collection),
// this is per-instance and sees the level and the fields, which a rendered console line loses.
//
// IsEnabled is always true so a test sees Debug/Trace lines too, whatever the host's configured
// minimum happens to be.
public class CapturingLogger : ILogger
{
    private readonly List<LogEntry> _entries = [];

    // A snapshot: components under test log from background loops, so handing out the live list
    // would let an assertion enumerate it mid-write.
    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_entries) return [.. _entries]; }
    }

    public IEnumerable<LogEntry> AtLevel(LogLevel level) => Entries.Where(e => e.Level == level);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        // Every ILogger call built from a message template hands its state over as the key/value
        // list below (message template included, under "{OriginalFormat}").
        var properties = state is IReadOnlyList<KeyValuePair<string, object?>> pairs
            ? pairs.ToDictionary(p => p.Key, p => p.Value)
            : [];
        var entry = new LogEntry(logLevel, formatter(state, exception), exception, properties);
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }
}

// The ILogger<T> a constructor-injected component asks for.
public sealed class CapturingLogger<T> : CapturingLogger, ILogger<T>;
