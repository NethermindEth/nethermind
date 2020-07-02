﻿//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.RoutingTable
{
    public class NodeBucket
    {
        private readonly object _nodeBucketLock = new object();
        private readonly SortedSet<NodeBucketItem> _items;

        public NodeBucket(int distance, int bucketSize)
        {
            _items = new SortedSet<NodeBucketItem>(LastContactTimeComparer.Instance);
            Distance = distance;
            BucketSize = bucketSize;
        }

        /// <summary>
        /// Distance from Master Node
        /// </summary>
        public int Distance { get; }
        public int BucketSize { get; }

        public IReadOnlyCollection<NodeBucketItem> Items
        {
            get
            {
                lock (_nodeBucketLock)
                {
                    return _items.ToArray();
                }
            }
        }
        
        public IReadOnlyCollection<NodeBucketItem> BondedItems
        {
            get
            {
                lock (_nodeBucketLock)
                {
                    // this ToArray call is responsible for around 1.5% (1GB) allocations on a sample Goerli fast sync
                    return _items.Where(i => (DateTime.UtcNow - i.LastContactTime) < TimeSpan.FromDays(2)).ToArray();
                }
            }
        }

        public NodeAddResult AddNode(Node node)
        {
            lock (_nodeBucketLock)
            {
                if (_items.Count < BucketSize)
                {
                    NodeBucketItem item = new NodeBucketItem(node);
                    if (!_items.Contains(item))
                    {
                        _items.Add(item);
                    }
                    return NodeAddResult.Added();
                }

                NodeBucketItem evictionCandidate = GetEvictionCandidate();
                return NodeAddResult.Full(evictionCandidate);
            }  
        }

        public void RemoveNode(Node node)
        {
            lock (_nodeBucketLock)
            {
                NodeBucketItem item = new NodeBucketItem(node);
                if (_items.Contains(item))
                {
                    _items.Remove(item);
                }
            }
        }

        public void ReplaceNode(Node nodeToRemove, Node nodeToAdd)
        {
            lock (_nodeBucketLock)
            {
                NodeBucketItem item = new NodeBucketItem(nodeToRemove);
                if (_items.Contains(item))
                {
                    _items.Remove(item);
                }
                item = new NodeBucketItem(nodeToAdd);
                if (!_items.Contains(item))
                {
                    _items.Add(item);
                }
            }
        }

        public void RefreshNode(Node node)
        {
            lock (_nodeBucketLock)
            {
                NodeBucketItem item = new NodeBucketItem(node);
                NodeBucketItem bucketItem = _items.FirstOrDefault(x => x.Equals(item));
                bucketItem?.OnContactReceived();
            }
        }

        private NodeBucketItem GetEvictionCandidate()
        {
            return _items.Last();
        }

        private class LastContactTimeComparer : IComparer<NodeBucketItem>
        {
            private LastContactTimeComparer()
            {
            }
            
            private static LastContactTimeComparer _lastContactTimeComparer;

            public static LastContactTimeComparer Instance
            {
                get
                {
                    if (_lastContactTimeComparer == null)
                    {
                        LazyInitializer.EnsureInitialized(ref _lastContactTimeComparer, () => new LastContactTimeComparer());
                    }

                    return _lastContactTimeComparer;
                }
            }
            
            public int Compare(NodeBucketItem x, NodeBucketItem y)
            {
                if (ReferenceEquals(x, y))
                {
                    return 0;
                }

                if (x == null)
                {
                    return -1;
                }

                if (y == null)
                {
                    return 1;
                }                

                //checking if both objects are the same
                if (x.Equals(y))
                {
                    return 0;
                }

                int timeComparison = x.LastContactTime.CompareTo(y.LastContactTime);
                if (timeComparison == 0)
                {
                    //last contact time is the same, but items are not the same, selecting higher id as higher item
                    return x.Node.GetHashCode() > y.Node.GetHashCode() ? 1 : -1;
                }

                return timeComparison;
            }
        }
    }
}