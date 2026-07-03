using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

namespace AiGateway.Logging;

/// <summary>
/// Owns the observable collection of <see cref="LogEntry"/> bound to the UI.
///
/// Performance notes:
/// - New entries are buffered and flushed to the bound collection in batches on a
///   ~10 Hz <see cref="DispatcherTimer"/>, so a burst of requests costs one UI update
///   instead of one dispatcher round-trip each.
/// - Completion updates mutate observable scalar properties directly; WPF marshals
///   those PropertyChanged notifications to the UI thread on its own, so no per-request
///   dispatcher call is needed.
/// - When there is no WPF dispatcher (e.g. tests), inserts happen inline.
/// </summary>
public sealed class LogService
{
    private readonly Dictionary<int, LogEntry> _bySession = new();
    private readonly List<LogEntry> _pending = new();
    private readonly object _gate = new();

    private readonly Dispatcher? _dispatcher;
    private readonly DispatcherTimer? _flushTimer;

    /// <summary>Maximum entries kept in memory; oldest are trimmed.</summary>
    public int MaxEntries { get; set; } = 5000;

    public ObservableCollection<LogEntry> Entries { get; } = new();

    public LogService()
    {
        _dispatcher = Application.Current?.Dispatcher;

        if (_dispatcher is not null)
        {
            _flushTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(100),
            };
            _flushTimer.Tick += (_, _) => FlushPending();
            _flushTimer.Start();
        }
    }

    /// <summary>Record a new in-flight request. Safe to call from any thread.</summary>
    public void AddRequest(LogEntry entry)
    {
        bool flushInline;
        lock (_gate)
        {
            _bySession[entry.SessionId] = entry;
            _pending.Add(entry);
            flushInline = _dispatcher is null;
        }

        // No UI thread (tests): insert immediately so state is observable synchronously.
        if (flushInline)
            FlushPending();
    }

    /// <summary>Complete a previously-recorded request with response data.</summary>
    public void CompleteResponse(int sessionId, int statusCode, double elapsedMs, long responseSize)
    {
        LogEntry? entry;
        lock (_gate)
        {
            _bySession.TryGetValue(sessionId, out entry);
            _bySession.Remove(sessionId);
        }

        if (entry is null)
            return;

        // Scalar property changes: WPF marshals these to the UI thread automatically.
        entry.StatusCode = statusCode;
        entry.ElapsedMs = elapsedMs;
        entry.ResponseSize = responseSize;
        entry.Completed = true;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _bySession.Clear();
            _pending.Clear();
        }
        RunOnUi(Entries.Clear);
    }

    /// <summary>Drain buffered entries into the bound collection (runs on the UI thread).</summary>
    private void FlushPending()
    {
        LogEntry[] batch;
        lock (_gate)
        {
            if (_pending.Count == 0)
                return;
            batch = _pending.ToArray();
            _pending.Clear();
        }

        // Insert in arrival order at the front so the newest ends up on top.
        foreach (var entry in batch)
            Entries.Insert(0, entry);

        while (Entries.Count > MaxEntries)
            Entries.RemoveAt(Entries.Count - 1);
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess())
            action();
        else
            _dispatcher.BeginInvoke(action);
    }
}
