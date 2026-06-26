namespace EventCapture.Core.Capture;

internal sealed class ReplaySegmentBuffer : IDisposable
{
    private sealed class Entry
    {
        public required string Path { get; init; }
        public required long StartTimestamp { get; init; }
        public required long EndTimestamp { get; init; }
        public int LeaseCount { get; set; }
        public bool DeletePending { get; set; }
    }

    private readonly object _sync = new();
    private readonly List<Entry> _entries = [];
    private readonly long _retentionMilliseconds;
    private bool _disposed;

    public ReplaySegmentBuffer(TimeSpan retention)
    {
        _retentionMilliseconds = Math.Max(1_000, (long)retention.TotalMilliseconds);
    }

    public void Add(string path, long startTimestamp, long endTimestamp)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        lock (_sync)
        {
            ThrowIfDisposed();
            _entries.Add(new Entry
            {
                Path = path,
                StartTimestamp = startTimestamp,
                EndTimestamp = Math.Max(startTimestamp + 1, endTimestamp)
            });

            PruneCore(endTimestamp - _retentionMilliseconds);
        }
    }

    public Lease Acquire(long fromTimestamp, long toTimestamp)
    {
        lock (_sync)
        {
            ThrowIfDisposed();

            var selected = _entries
                .Where(entry => entry.EndTimestamp > fromTimestamp && entry.StartTimestamp < toTimestamp)
                .OrderBy(entry => entry.StartTimestamp)
                .ToArray();

            foreach (Entry entry in selected) entry.LeaseCount++;

            return new Lease(
                selected.Select(entry => new Segment(entry.Path, entry.StartTimestamp, entry.EndTimestamp)).ToArray(),
                () => Release(selected));
        }
    }

    public void Prune(long nowTimestamp)
    {
        lock (_sync)
        {
            if (_disposed) return;
            PruneCore(nowTimestamp - _retentionMilliseconds);
        }
    }

    private void PruneCore(long cutoffTimestamp)
    {
        for (int index = _entries.Count - 1; index >= 0; index--)
        {
            Entry entry = _entries[index];
            if (entry.EndTimestamp >= cutoffTimestamp) continue;

            _entries.RemoveAt(index);
            if (entry.LeaseCount == 0) TryDelete(entry.Path);
            else entry.DeletePending = true;
        }
    }

    private void Release(IEnumerable<Entry> entries)
    {
        lock (_sync)
        {
            foreach (Entry entry in entries)
            {
                entry.LeaseCount = Math.Max(0, entry.LeaseCount - 1);
                if (entry.LeaseCount == 0 && entry.DeletePending) TryDelete(entry.Path);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;

            foreach (Entry entry in _entries)
            {
                if (entry.LeaseCount == 0) TryDelete(entry.Path);
                else entry.DeletePending = true;
            }

            _entries.Clear();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    internal sealed record Segment(string Path, long StartTimestamp, long EndTimestamp);

    internal sealed class Lease : IDisposable
    {
        private Action? _release;

        public Lease(IReadOnlyList<Segment> segments, Action release)
        {
            Segments = segments;
            _release = release;
        }

        public IReadOnlyList<Segment> Segments { get; }

        public void Dispose()
        {
            Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }
}
