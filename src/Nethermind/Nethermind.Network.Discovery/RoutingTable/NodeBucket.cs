// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Diagnostics;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.RoutingTable;

[DebuggerDisplay("{BondedItemsCount} bonded item(s)")]
public class NodeBucket
{
    private readonly Lock _nodeBucketLock = new();
    private readonly LinkedList<NodeBucketItem> _items;
    private readonly float _dropFullBucketProbability;

    public NodeBucket(int distance, int bucketSize, float dropFullBucketProbability = 0.0f)
    {
        _items = new LinkedList<NodeBucketItem>();
        Distance = distance;
        BucketSize = bucketSize;
        _dropFullBucketProbability = dropFullBucketProbability;
    }

    /// <summary>
    /// Distance from Master Node
    /// </summary>
    public int Distance { get; }

    public int BucketSize { get; }

    public bool AnyBondedItems()
    {
        foreach (NodeBucketItem _ in BondedItems)
        {
            return true;
        }

        return false;
    }

    public BondedItemsEnumerator BondedItems
        => new(this);

    public struct BondedItemsEnumerator : IEnumerator<NodeBucketItem>, IEnumerable<NodeBucketItem>
    {
        private readonly NodeBucket _nodeBucket;
        private LinkedListNode<NodeBucketItem>? _currentNode;
        private readonly DateTime _referenceTime;

        public BondedItemsEnumerator(NodeBucket nodeBucket)
        {
            _nodeBucket = nodeBucket;
            lock (_nodeBucket._nodeBucketLock)
            {
                _currentNode = nodeBucket._items.Last;
            }
            _referenceTime = DateTime.UtcNow;
            Current = null!;
        }

        public NodeBucketItem Current { get; private set; }

        readonly object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            lock (_nodeBucket._nodeBucketLock)
            {
                while (_currentNode is not null)
                {
                    Current = _currentNode.Value;
                    _currentNode = _currentNode.Previous;
                    if (Current.IsBonded(_referenceTime))
                    {
                        return true;
                    }
                }
            }

            Current = null!;
            return false;
        }

        void IEnumerator.Reset() => throw new NotSupportedException();

        public readonly void Dispose()
        {
        }
        public readonly BondedItemsEnumerator GetEnumerator() => this;

        readonly IEnumerator<NodeBucketItem> IEnumerable<NodeBucketItem>.GetEnumerator()
            => GetEnumerator();

        readonly IEnumerator IEnumerable.GetEnumerator()
            => GetEnumerator();
    }

    public int BondedItemsCount
    {
        get
        {
            lock (_nodeBucketLock)
            {
                int result = _items.Count;
                LinkedListNode<NodeBucketItem>? node = _items.Last;
                DateTime utcNow = DateTime.UtcNow;
                while (node is not null)
                {
                    if (node.Value.IsBonded(utcNow))
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

            if (Random.Shared.NextSingle() < _dropFullBucketProbability)
            {
                NodeBucketItem item = new(node, DateTime.UtcNow);
                if (!_items.Contains(item))
                {
                    _items.AddFirst(item);
                    _items.RemoveLast();
                }

                return NodeAddResult.Dropped();
            }

            NodeBucketItem evictionCandidate = GetEvictionCandidate();
            return NodeAddResult.Full(evictionCandidate);
        }
    }

    public void ReplaceNode(Node nodeToRemove, Node nodeToAdd)
    {
        lock (_nodeBucketLock)
        {
            NodeBucketItem item = new(nodeToRemove, DateTime.UtcNow);
            if (_items.Remove(item))
            {
                AddNode(nodeToAdd);
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
