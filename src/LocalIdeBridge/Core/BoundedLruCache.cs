namespace LocalIdeBridge.Core;

/// <summary>
/// Thread-safe LRU cache strictly bounded by both entry count and total byte weight.
/// Prevents RAM growth proportional to repository size or session duration.
/// </summary>
public sealed class BoundedLruCache<TKey, TValue> where TKey : notnull
{
    private sealed record CacheNode(TKey Key, TValue Value, long SizeBytes);

    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private readonly Dictionary<TKey, LinkedListNode<CacheNode>> _map;
    private readonly LinkedList<CacheNode> _lru = new();
    private readonly object _sync = new();
    private long _currentBytes;

    public BoundedLruCache(int maxEntries, long maxBytes, IEqualityComparer<TKey>? comparer = null)
    {
        _maxEntries = Math.Max(1, maxEntries);
        _maxBytes = Math.Max(1024, maxBytes);
        _map = new Dictionary<TKey, LinkedListNode<CacheNode>>(_maxEntries, comparer);
    }

    public int Count { get { lock (_sync) return _map.Count; } }
    public long CurrentBytes { get { lock (_sync) return _currentBytes; } }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_sync)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value, long sizeBytes)
    {
        var weight = Math.Max(1, sizeBytes);
        if (weight > _maxBytes)
            return; // Oversized single item is never cached in memory.

        lock (_sync)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _currentBytes -= existing.Value.SizeBytes;
                _lru.Remove(existing);
                _map.Remove(key);
            }

            while (_map.Count >= _maxEntries || (_currentBytes + weight > _maxBytes && _lru.Last is not null))
            {
                var tail = _lru.Last!;
                _currentBytes -= tail.Value.SizeBytes;
                _map.Remove(tail.Value.Key);
                _lru.RemoveLast();
            }

            var added = _lru.AddFirst(new CacheNode(key, value, weight));
            _map[key] = added;
            _currentBytes += weight;
        }
    }

    public void Invalidate(TKey key)
    {
        lock (_sync)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _currentBytes -= node.Value.SizeBytes;
                _lru.Remove(node);
                _map.Remove(key);
            }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _map.Clear();
            _lru.Clear();
            _currentBytes = 0;
        }
    }
}

/// <summary>
/// Fixed-capacity circular buffer with monotonic sequence IDs.
/// Guarantees O(1) insertion and bounded memory for TUI activity feeds and SSE replay.
/// </summary>
public sealed class BoundedRingBuffer<T>
{
    public readonly record struct SequencedItem(long Sequence, T Item);

    private readonly SequencedItem[] _buffer;
    private readonly object _sync = new();
    private int _head;
    private int _count;
    private long _nextSequence = 1;

    public int Capacity => _buffer.Length;

    public BoundedRingBuffer(int capacity)
    {
        _buffer = new SequencedItem[Math.Max(8, capacity)];
    }

    public long Push(T item)
    {
        lock (_sync)
        {
            var seq = _nextSequence++;
            var idx = (_head + _count) % _buffer.Length;
            if (_count == _buffer.Length)
            {
                _buffer[_head] = new SequencedItem(seq, item);
                _head = (_head + 1) % _buffer.Length;
            }
            else
            {
                _buffer[idx] = new SequencedItem(seq, item);
                _count++;
            }
            return seq;
        }
    }

    public IReadOnlyList<T> SnapshotLatest(int maxItems)
    {
        lock (_sync)
        {
            var take = Math.Clamp(maxItems, 0, _count);
            var list = new List<T>(take);
            var startOffset = _count - take;
            for (var i = 0; i < take; i++)
            {
                var idx = (_head + startOffset + i) % _buffer.Length;
                list.Add(_buffer[idx].Item);
            }
            return list;
        }
    }

    public IReadOnlyList<SequencedItem> SnapshotAfter(long afterSequence, int maxItems = 100)
    {
        lock (_sync)
        {
            var list = new List<SequencedItem>();
            for (var i = 0; i < _count && list.Count < maxItems; i++)
            {
                var entry = _buffer[(_head + i) % _buffer.Length];
                if (entry.Sequence > afterSequence)
                    list.Add(entry);
            }
            return list;
        }
    }

    public long LatestSequence
    {
        get { lock (_sync) return _nextSequence - 1; }
    }
}
