// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


namespace Nethermind.Kademlia;

public class KBucket<TNode, TKadKey>(int k)
    where TNode : notnull
    where TKadKey : notnull
{
    private readonly int _k = k;
    private DoubleEndedLru<TNode, TKadKey> _items = new(k);
    private DoubleEndedLru<TNode, TKadKey> _replacement = new(k);

    public int Count => _items.Count;

    private TNode[] _cachedArray = [];

    /// <summary>
    /// Add or refresh a node entry.
    /// Used when any traffic is received, or when seeding a node.
    /// Return the last entry in a bucket to refresh when bucket is full.
    /// </summary>
    /// <param name="item"></param>
    /// <returns></returns>
    public BucketAddResult TryAddOrRefresh(in TKadKey hash, TNode item, out TNode? toRefresh)
    {
        TNode? previous = _items.GetByHash(hash);
        BucketAddResult addResult = _items.AddOrRefresh(hash, item);
        if (addResult == BucketAddResult.Added ||
            (addResult == BucketAddResult.Refreshed && ShouldUpdateCachedArray(previous, item)))
        {
            _cachedArray = _items.GetAll();
        }

        // Either added or refreshed
        if (addResult != BucketAddResult.Full)
        {
            toRefresh = default;
            return addResult;
        }

        _replacement.AddOrRefresh(hash, item);
        _items.TryGetLast(out toRefresh);
        return BucketAddResult.Full;
    }

    public TNode[] GetAll() => _cachedArray;

    public (TKadKey, TNode)[] GetAllWithHash() => _items.GetAllWithHash();

    public bool RemoveAndReplace(in TKadKey hash)
    {
        if (!_items.Remove(hash)) return false;

        if (_replacement.TryPopHead(out TKadKey replacementHash, out TNode? replacement))
        {
            _items.AddOrRefresh(replacementHash, replacement!);
        }
        _cachedArray = _items.GetAll();

        return true;
    }

    public void Clear()
    {
        _items = new DoubleEndedLru<TNode, TKadKey>(_k);
        _replacement = new DoubleEndedLru<TNode, TKadKey>(_k);
        _cachedArray = _items.GetAll();
    }

    public bool ContainsNode(in TKadKey hash) => _items.Contains(hash);

    public TNode? GetByHash(TKadKey hash) => _items.GetByHash(hash);

    private static bool ShouldUpdateCachedArray(TNode? previous, TNode item)
    {
        if (previous is null)
        {
            return false;
        }

        return typeof(TNode).IsValueType
            ? !EqualityComparer<TNode>.Default.Equals(previous, item)
            : !ReferenceEquals(previous, item);
    }
}
