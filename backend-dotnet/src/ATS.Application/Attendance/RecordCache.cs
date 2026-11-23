using System.Diagnostics;

namespace Ats.Application.Attendance;

/// <summary>
/// In-process cache that lets the several requests of one dashboard page share one computation
/// (<c>app/services/record_cache.py</c>):
/// <list type="bullet">
/// <item>single flight: identical requests arriving together wait for one computation (up to 120 s);</item>
/// <item>bounded: least-recently-used entries are evicted beyond <see cref="MaxDayRecords"/>, and entries expire after
/// <see cref="TtlSeconds"/>;</item>
/// <item>never stale: keys contain everything the result depends on, and <see cref="WriteVersion"/> changes on every save
/// that touches timesheet days or uploads (bumped by the EF interceptor in Infrastructure).</item>
/// </list>
/// Cached values are shared between requests and are immutable.
/// </summary>
public sealed class RecordCache
{
    public const int TtlSeconds = 300;

    /// <summary>About 250 bytes per day record: at most ~250 MB, enough for 10,000 people x 90 days.</summary>
    public const int MaxDayRecords = 1_000_000;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(120);

    private readonly Lock _lock = new();
    private readonly LinkedList<string> _lru = new();
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly Dictionary<string, TaskCompletionSource> _inflight = [];
    private long _total;
    private long _version;

    private sealed record Entry(LinkedListNode<string> Node, long ExpiresAt, int Size, object Value);

    /// <summary>The memory budget in day records (<see cref="MaxDayRecords"/>; tests lower it).</summary>
    internal int Budget { get; set; } = MaxDayRecords;

    /// <summary>Day records held now, and the number of entries (for the budget tests).</summary>
    internal (long Total, int Entries) Usage
    {
        get
        {
            lock (_lock)
            {
                return (_total, _entries.Count);
            }
        }
    }

    internal bool Contains(string key)
    {
        lock (_lock)
        {
            return _entries.ContainsKey(key);
        }
    }

    public long WriteVersion => Interlocked.Read(ref _version);

    public void BumpWriteVersion() => Interlocked.Increment(ref _version);

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _lru.Clear();
            _total = 0;
        }
    }

    public async Task<T> GetOrComputeAsync<T>(string key, int size, Func<Task<T>> compute)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(compute);
        TaskCompletionSource? waiter;
        lock (_lock)
        {
            if (TryGet(key, out var hit))
            {
                return (T)hit;
            }

            if (!_inflight.TryGetValue(key, out waiter))
            {
                _inflight[key] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        if (waiter is not null)
        {
            try
            {
                // WaitAsync stops its timer as soon as the other request finishes (CA2027: Task.Delay in
                // Task.WhenAny would keep a timer running for the full wait).
                await waiter.Task.WaitAsync(Wait).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The other request is taking too long: compute here instead of waiting any longer.
            }

            lock (_lock)
            {
                if (TryGet(key, out var hit))
                {
                    return (T)hit;
                }
            }

            return await compute().ConfigureAwait(false);
        }

        try
        {
            var value = await compute().ConfigureAwait(false);
            lock (_lock)
            {
                Store(key, size, value);
            }

            return value;
        }
        finally
        {
            lock (_lock)
            {
                if (_inflight.Remove(key, out var done))
                {
                    done.SetResult();
                }
            }
        }
    }

    private static long Now => Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    private bool TryGet(string key, out object value)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAt >= Now)
            {
                _lru.Remove(entry.Node);
                _lru.AddLast(entry.Node);
                value = entry.Value;
                return true;
            }

            Drop(key);
        }

        value = null!;
        return false;
    }

    private void Drop(string key)
    {
        if (_entries.Remove(key, out var entry))
        {
            _lru.Remove(entry.Node);
            _total -= entry.Size;
        }
    }

    private void Store(string key, int size, object value)
    {
        if (size > Budget)
        {
            return; // too big to keep; the caller still gets the value
        }

        Drop(key);
        _entries[key] = new Entry(_lru.AddLast(key), Now + TtlSeconds, size, value);
        _total += size;
        while (_total > Budget && _lru.First is { } oldest)
        {
            Drop(oldest.Value);
        }
    }
}
