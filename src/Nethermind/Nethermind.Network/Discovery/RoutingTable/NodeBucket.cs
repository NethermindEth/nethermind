//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.Diagnostics;
using System.Linq;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.RoutingTable
{
    [DebuggerDisplay("{BondedItemsCount} bonded item(s)")]
    public class NodeBucket
    {
        private readonly object _nodeBucketLock = new object();
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
                    var node = _items.Last;
                    while (node != null)
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
                    var node = _items.Last;
                    while (node != null)
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
                    NodeBucketItem item = new NodeBucketItem(node, DateTime.UtcNow);
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
                NodeBucketItem item = new NodeBucketItem(nodeToRemove, DateTime.Now);
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
                NodeBucketItem item = new NodeBucketItem(node, DateTime.UtcNow);
                var bucketItem = _items.Find(item);
                if (bucketItem != null)
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
}
