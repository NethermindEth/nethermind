// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Kademlia;

public class KBucket<TNode, TKadKey>(int k)
    where TNode : notnull
    where TKadKey : notnull
{
    public const int DefaultReplacementCacheSize = 16;

    private readonly DoubleEndedLru<TKadKey, TNode> _items = new(k);
    private readonly DoubleEndedLru<TKadKey, TNode> _replacement = new(GetReplacementCacheSize(k));
    private readonly bool _cacheItems = k <= DefaultReplacementCacheSize;

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
        BucketAddResult addResult = _items.AddOrRefresh(hash, item, out TNode? previous);
        if (_cacheItems
            && (addResult == BucketAddResult.Added
                || (addResult == BucketAddResult.Refreshed && ShouldUpdateCachedArray(previous, item))))
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

    public TNode[] GetAll() => _items.GetAll();

    internal TNode[] GetAllCached() => _cacheItems ? _cachedArray : _items.GetAll();

    public (TKadKey, TNode)[] GetAllWithHash() => _items.GetAllWithKey();

    internal int CopyAllWithHash((TKadKey Hash, TNode Node)[] destination) => _items.CopyAllWithKey(destination);

    public bool Remove(in TKadKey hash)
    {
        if (!_items.Remove(hash)) return false;

        UpdateCachedArray();
        return true;
    }

    /// <summary>
    /// Promotes the most recently cached replacement that <paramref name="canPromote"/> accepts, discarding the
    /// candidates it rejects.
    /// </summary>
    /// <param name="canPromote">Admission predicate for a candidate, or <see langword="null"/> to accept the first.</param>
    /// <returns><see langword="true"/> when a replacement was promoted.</returns>
    public bool TryPromote(Func<TNode, bool>? canPromote)
    {
        while (_replacement.TryPopHead(out TKadKey replacementHash, out TNode? replacement))
        {
            if (canPromote is not null && !canPromote(replacement!)) continue;

            _items.AddOrRefresh(replacementHash, replacement!);
            UpdateCachedArray();
            return true;
        }

        return false;
    }

    public void Clear()
    {
        _items.Clear();
        _replacement.Clear();
        _cachedArray = [];
    }

    public bool ContainsNode(in TKadKey hash) => _items.Contains(hash);

    public TNode? GetByHash(TKadKey hash) => _items.GetByKey(hash);

    internal bool TryGetStored(in TKadKey hash, [MaybeNullWhen(false)] out TNode node)
        => _items.TryGetValue(hash, out node) || _replacement.TryGetValue(hash, out node);

    private void UpdateCachedArray()
    {
        if (_cacheItems) _cachedArray = _items.GetAll();
    }

    private static bool ShouldUpdateCachedArray(TNode? previous, TNode item)
        => previous is not null &&
            (typeof(TNode).IsValueType
            ? !EqualityComparer<TNode>.Default.Equals(previous, item)
            : !ReferenceEquals(previous, item));

    private static int GetReplacementCacheSize(int bucketSize)
        => bucketSize < DefaultReplacementCacheSize ? bucketSize : DefaultReplacementCacheSize;
}
