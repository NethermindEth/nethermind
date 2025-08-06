using System.Collections.Concurrent;

namespace Lantern.Discv5.WireProtocol.Session;

public class LruCache<TKey, TValue>(int capacity) where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, LinkedListNode<CacheItem>> _cache = new(capacity, capacity);
    private readonly LinkedList<CacheItem> _lruList = new();
    private readonly object _lock = new();

    public int Count => _cache.Count;

    public TValue? Get(TKey key)
    {
        if (!_cache.TryGetValue(key, out var node))
            return default;

        var value = node.Value.Value;
        RefreshNode(node);

        return value;
    }

    public void Add(TKey key, TValue value)
    {
        if (_cache.TryGetValue(key, out var existingNode))
        {
            RefreshNode(existingNode);
            existingNode.Value.Value = value;
        }
        else
        {
            if (_cache.Count >= capacity)
            {
                RemoveFirst();
            }

            var cacheItem = new CacheItem(key, value);
            var node = new LinkedListNode<CacheItem>(cacheItem);

            _lruList.AddLast(node);
            _cache[key] = node;
        }
    }

    private void RefreshNode(LinkedListNode<CacheItem> node)
    {
        lock (_lock)
        {
            _lruList.Remove(node);
            _lruList.AddLast(node);
        }
    }

    private void RemoveFirst()
    {
        var node = _lruList.First;
        _lruList.RemoveFirst();

        if (node == null)
            return;

        _cache.TryRemove(node.Value.Key, out _);
    }

    private sealed class CacheItem(TKey k, TValue v)
    {
        public TKey Key { get; } = k;
        public TValue Value { get; set; } = v;
    }
}
