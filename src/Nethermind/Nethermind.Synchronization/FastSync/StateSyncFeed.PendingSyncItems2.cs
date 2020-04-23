//  Copyright (c) 2018 Demerzel Solutions Limited
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
// 

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Priority_Queue;

namespace Nethermind.Synchronization.FastSync
{
    public partial class StateSyncFeed
    {
        internal class PendingSyncItems2 : IPendingSyncItems
        {
            private FastPriorityQueue<StateSyncItem> _queue = new FastPriorityQueue<StateSyncItem>(1024 * 1024);
            
            private ConcurrentStack<StateSyncItem>[] _allStacks = new ConcurrentStack<StateSyncItem>[7];

            private decimal _lastSyncProgress;
            private uint _maxStorageRightness; // for priority calculation (prefer left)
            private uint _maxRightness; // for priority calculation (prefer left)

            public byte MaxStorageLevel { get; set; }
            public byte MaxStateLevel { get; set; }

            public PendingSyncItems2()
            {
                for (int i = 0; i < _allStacks.Length; i++)
                {
                    _allStacks[i] = new ConcurrentStack<StateSyncItem>();
                }
            }
            
            public void PushToSelectedStream(StateSyncItem stateSyncItem, decimal progress)
            {
                _lastSyncProgress = progress;
                float priority = CalculatePriority(stateSyncItem.NodeDataType, stateSyncItem.Level, stateSyncItem.Rightness);
                lock (_queue)
                {
                    _queue.Enqueue(stateSyncItem, priority);
                }
            }

            private string LevelsDescription => $"{MaxStorageLevel:D2} {_maxStorageRightness:D8} | {MaxStateLevel:D2} {_maxRightness:D8}";
            public string Description => $"{_queue.Count}";
            public int Count => _allStacks.Sum(n => n?.Count ?? 0);

            public StateSyncItem PeekState()
            {
                lock (_queue)
                {
                    return _queue.Count == 0 ? null : _queue.First;
                }
            }

            private float CalculatePriority(NodeDataType nodeDataType, byte level, uint rightness)
            {
                if (nodeDataType == NodeDataType.Code)
                {
                    return 1000f;
                }
                
                switch (nodeDataType)
                {
                    case NodeDataType.Storage:
                        MaxStorageLevel = Math.Max(MaxStorageLevel, level);
                        _maxStorageRightness = Math.Max(_maxStorageRightness, rightness);
                        break;
                    case NodeDataType.State:
                        MaxStateLevel = Math.Max(MaxStateLevel, level);
                        _maxRightness = Math.Max(_maxRightness, rightness);
                        break;
                }
                
                float priority = CalculatePriority(level, rightness);
                switch (nodeDataType)
                {
                    case NodeDataType.Storage:
                        return 2000f + priority;
                    case NodeDataType.State:
                        return 3000f + priority;
                }

                return priority;
            }

            private float CalculatePriority(byte level, uint rightness)
            {
                // the more synced we are the more to the right we want to go - hence the sync progress modifier at the end
                // we want to keep more or less to the same side (left or right - we chose left) so we punish
                // the high child indices
                // we want to go deep first so we add bonus for the depth
                float priority = 1.00f - (float) level / Math.Max(MaxStateLevel, (byte) 1) + (float) rightness / Math.Max(_maxRightness, 1) - (float) _lastSyncProgress / 2;
                return priority;
            }
            
            public void Clear()
            {
                lock (_queue)
                {
                    _queue.Clear();
                }
            }

            private bool TryTake(out StateSyncItem node)
            {
                lock (_queue)
                {
                    if (_queue.Count > 0)
                    {
                        node = _queue.Dequeue();
                        return true;
                    }
                }

                node = null;
                return false;
            }

            public List<StateSyncItem> TakeBatch(int maxSize)
            {
                // the limitation is to prevent an early explosion of request sizes with low level nodes
                // the moment we find the first leaf we will know something more about the tree structure and hence
                // prevent lot of Stream2 entries to stay in memory for a long time 
                int length = MaxStateLevel == 64 ? maxSize : Math.Max(1, (int) (maxSize * ((decimal) MaxStateLevel / 64) * ((decimal) MaxStateLevel / 64)));

                List<StateSyncItem> requestHashes = new List<StateSyncItem>();
                for (int i = 0; i < length; i++)
                {
                    if (TryTake(out StateSyncItem requestItem))
                    {
                        requestHashes.Add(requestItem);
                    }
                    else
                    {
                        break;
                    }
                }

                return requestHashes;
            }

            public string RecalculatePriorities()
            {
                Stopwatch stopwatch = new Stopwatch();
                stopwatch.Start();

                string reviewMessage = $"Node sync queues review ({LevelsDescription}):" + Environment.NewLine;
                reviewMessage += $"  before {Description}" + Environment.NewLine;

                lock (_queue)
                {
                    foreach (StateSyncItem stateSyncItem in _queue)
                    {
                        _queue.UpdatePriority(stateSyncItem, CalculatePriority(stateSyncItem.NodeDataType, stateSyncItem.Level, stateSyncItem.Rightness));
                    }
                }

                reviewMessage += $"  after {Description}" + Environment.NewLine;

                stopwatch.Stop();
                reviewMessage += $"  time spent in review: {stopwatch.ElapsedMilliseconds}ms";
                return reviewMessage;
            }
        }
    }
}