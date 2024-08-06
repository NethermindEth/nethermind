// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Kademlia;

public class KBucket<TNode>(int k) where TNode : notnull
{
    private DoubleEndedLru<TNode> _items = new(k);
    private DoubleEndedLru<TNode> _replacement = new(k); // Well, the replacement does not have to be k. Could be much lower.

    public int Count => _items.Count;

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
        // TODO: Seems like a good candidate to cache
        return _items.GetAll();
    }

    public void RemoveAndReplace(in ValueHash256 hash)
    {
        if (_items.Remove(hash))
        {
            if (_replacement.TryPopHead(out TNode? replacement))
            {
                _items.AddOrRefresh(hash, replacement!);
            }
        }
    }

    public void Remove(in ValueHash256 hash)
    {
        _items.Remove(hash);
        _replacement.Remove(hash);
    }
}
