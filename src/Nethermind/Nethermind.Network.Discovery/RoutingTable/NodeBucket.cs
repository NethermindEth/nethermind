// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Diagnostics;

using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.RoutingTable;

[DebuggerDisplay("{Count} bonded item(s)")]
public class NodeBucket : IEnumerable<NodeBucketItem>
{
    private readonly object _nodeBucketLock = new();
    private readonly LinkedList<NodeBucketItem> _items;
    private int _count;

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

    public int Count => _count;

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
                    _count++;
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
                _count--;
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

    public BondedItemsEnumerator GetEnumerator() => new(this);
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    IEnumerator<NodeBucketItem> IEnumerable<NodeBucketItem>.GetEnumerator() => GetEnumerator();

    public struct BondedItemsEnumerator : IEnumerator<NodeBucketItem>
    {
        private LinkedListNode<NodeBucketItem>? _node;
        private NodeBucketItem _current;
        private readonly NodeBucket _bucket;

        internal BondedItemsEnumerator(NodeBucket bucket)
        {
            _node = null;
            _current = null!;
            _bucket = bucket;
        }

        public NodeBucketItem Current => _current;

        public bool MoveNext()
        {
            lock (_bucket._nodeBucketLock)
            {
                LinkedListNode<NodeBucketItem>? node = _node;
                if (node is null)
                {
                    _node = node = _bucket._items.Last;
                    if (node is null) return false;
                }
                else
                {
                    _node = node = node.Previous;
                    if (node is null) return false;
                }

                if (!node.Value.IsBonded)
                {
                    return false;
                }

                _current = node.Value;
                return true;
            }
        }

        public void Dispose() { }
        object IEnumerator.Current => Current;
        void IEnumerator.Reset() => throw new NotSupportedException();
    }
}
