// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Kademlia;

public class KBucket<TNode> where TNode : notnull
{
    private readonly int _k;
    private DoubleEndedLru<TNode> _items;
    private DoubleEndedLru<TNode> _replacement;

    public int Count => _items.Count;

    private TNode[] _cachedArray = Array.Empty<TNode>();

    public KBucket(int k)
    {
        _k = k;
        _items = new DoubleEndedLru<TNode>(k);
        _replacement = new DoubleEndedLru<TNode>(k);  // Well, the replacement does not have to be k. Could be much lower.
    }

    /// <summary>
    /// Add or refresh a node entry.
    /// Used when any traffic is received, or when seeding a node.
    /// Return the last entry in a bucket to refresh when bucket is full.
    /// </summary>
    /// <param name="item"></param>
    /// <returns></returns>
    public BucketAddResult TryAddOrRefresh(in ValueHash256 hash, TNode item, out TNode? toRefresh)
    {
        BucketAddResult addResult = _items.AddOrRefresh(hash, item);
        if (addResult == BucketAddResult.Added)
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

    public TNode[] GetAll()
    {
        return _cachedArray;
    }

    public IEnumerable<(ValueHash256, TNode)> GetAllWithHash()
    {
        return _items.GetAllWithHash();
    }

    public bool RemoveAndReplace(in ValueHash256 hash)
    {
        if (!_items.Remove(hash)) return false;

        if (_replacement.TryPopHead(out TNode? replacement))
        {
            _items.AddOrRefresh(hash, replacement!);
        }
        _cachedArray = _items.GetAll();

        return true;
    }

    public void Clear()
    {
        _items = new DoubleEndedLru<TNode>(_k);
        _replacement = new DoubleEndedLru<TNode>(_k);
        _cachedArray = _items.GetAll();
    }

    public bool ContainsNode(in ValueHash256 hash)
    {
        return _items.Contains(hash);
    }

    public TNode? GetByHash(ValueHash256 hash)
    {
        return _items.GetByHash(hash);
    }
}
