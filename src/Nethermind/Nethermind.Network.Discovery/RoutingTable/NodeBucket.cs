// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.RoutingTable;

[DebuggerDisplay("{BondedItemsCount} bonded item(s)")]
public class NodeBucket
{
    private readonly object _nodeBucketLock = new();
    private readonly LinkedList<NodeBucketItem> _items;

    public NodeBucket(int distance, int bucketSize)
    {
        _items = new LinkedList<NodeBucketItem>();
        Distance = distance;
        BucketSize = bucketSize;
    }

    /// <summary>
    /// Distance from Master Node
    /// </summary>
    public int Distance { get; }

    public int BucketSize { get; }

    public IEnumerable<NodeBucketItem> BondedItems
    {
        get
        {
            lock (_nodeBucketLock)
            {
                LinkedListNode<NodeBucketItem>? node = _items.Last;
                while (node is not null)
                {
                    if (!node.Value.IsBonded)
                    {
                        break;
                    }

                    yield return node.Value;
                    node = node.Previous;
                }
            }
        }
    }

    public int BondedItemsCount
    {
        get
        {
            lock (_nodeBucketLock)
            {
                int result = _items.Count;
                LinkedListNode<NodeBucketItem>? node = _items.Last;
                while (node is not null)
                {
                    if (node.Value.IsBonded)
                    {
                        break;
                    }

                    node = node.Previous;
                    result--;
                }

                return result;
            }
        }
    }

    public NodeAddResult AddNode(Node node)
    {
        lock (_nodeBucketLock)
        {
            if (_items.Count < BucketSize)
            {
                NodeBucketItem item = new(node, DateTime.UtcNow);
                if (!_items.Contains(item))
                {
                    _items.AddFirst(item);
                }

                return NodeAddResult.Added();
            }

            NodeBucketItem evictionCandidate = GetEvictionCandidate();
            return NodeAddResult.Full(evictionCandidate);
        }
    }

    public void ReplaceNode(Node nodeToRemove, Node nodeToAdd)
    {
        lock (_nodeBucketLock)
        {
            NodeBucketItem item = new(nodeToRemove, DateTime.Now);
            if (_items.Contains(item))
            {
                _items.Remove(item);
                AddNode(nodeToAdd);
            }
            else
            {
                throw new InvalidOperationException(
                    "Cannot replace non-existing node in the node table bucket");
            }
        }
    }

    public void RefreshNode(Node node)
    {
        lock (_nodeBucketLock)
        {
            NodeBucketItem item = new(node, DateTime.UtcNow);
            LinkedListNode<NodeBucketItem>? bucketItem = _items.Find(item);
            if (bucketItem is not null)
            {
                bucketItem.Value.OnContactReceived();
                _items.Remove(item);
                _items.AddFirst(item);
            }
        }
    }

    private NodeBucketItem GetEvictionCandidate()
    {
        return _items.Last();
    }
}
