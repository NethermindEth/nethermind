// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Nethermind.Synchronization.FastSync
{
    internal interface IPendingSyncItems
    {
        StateSyncItem? PeekState();
        string RecalculatePriorities();
        List<StateSyncItem> TakeBatch(int maxSize);
        void Clear();
        byte MaxStateLevel { get; set; }
        byte MaxStorageLevel { get; set; }
        int Count { get; }
        string Description { get; }
        void PushToSelectedStream(StateSyncItem item, decimal progress);
    }

    internal class PendingSyncItems : IPendingSyncItems
    {
        private ConcurrentStack<StateSyncItem>[] _allStacks = new ConcurrentStack<StateSyncItem>[7];

        private ConcurrentStack<StateSyncItem> CodeItems => _allStacks[0];

        private ConcurrentStack<StateSyncItem> StorageItemsPriority0 => _allStacks[1];
        private ConcurrentStack<StateSyncItem> StorageItemsPriority1 => _allStacks[2];
        private ConcurrentStack<StateSyncItem> StorageItemsPriority2 => _allStacks[3];

        private ConcurrentStack<StateSyncItem> StateItemsPriority0 => _allStacks[4];
        private ConcurrentStack<StateSyncItem> StateItemsPriority1 => _allStacks[5];
        private ConcurrentStack<StateSyncItem> StateItemsPriority2 => _allStacks[6];

        private decimal _lastSyncProgress;
        private uint _maxStorageRightness; // for priority calculation (prefer left)
        private uint _maxRightness; // for priority calculation (prefer left)

        public byte MaxStorageLevel { get; set; }
        public byte MaxStateLevel { get; set; }

        public PendingSyncItems()
        {
            for (int i = 0; i < _allStacks.Length; i++)
            {
                _allStacks[i] = new ConcurrentStack<StateSyncItem>();
            }
        }

        public void PushToSelectedStream(StateSyncItem stateSyncItem, decimal progress)
        {
            _lastSyncProgress = progress;
            double priority = CalculatePriority(stateSyncItem.NodeDataType, stateSyncItem.Level, stateSyncItem.Rightness);

            var selectedCollection = stateSyncItem.NodeDataType switch
            {
                NodeDataType.Code => CodeItems,
                NodeDataType.State when priority <= 0.5f => StateItemsPriority0,
                NodeDataType.State when priority <= 1.5f => StateItemsPriority1,
                NodeDataType.State => StateItemsPriority2,
                NodeDataType.Storage when priority <= 0.5f => StorageItemsPriority0,
                NodeDataType.Storage when priority <= 1.5f => StorageItemsPriority1,
                NodeDataType.Storage => StorageItemsPriority2,
                _ => throw new ArgumentOutOfRangeException()
            };

            selectedCollection.Push(stateSyncItem);
        }

        private string LevelsDescription => $"{MaxStorageLevel:D2} {_maxStorageRightness:D8} | {MaxStateLevel:D2} {_maxRightness:D8}";
        public string Description => $"{CodeItems?.Count ?? 0:D4} + {StorageItemsPriority0?.Count ?? 0:D6} {StorageItemsPriority1?.Count ?? 0:D6} {StorageItemsPriority2?.Count ?? 0:D6} + {StateItemsPriority0?.Count ?? 0:D6} {StateItemsPriority1?.Count ?? 0:D6} {StateItemsPriority2?.Count ?? 0:D6}";
        public int Count => _allStacks.Sum(n => n?.Count ?? 0);

        public StateSyncItem? PeekState()
        {
            StateItemsPriority0.TryPeek(out StateSyncItem? node);

            if (node is null)
            {
                StateItemsPriority1.TryPeek(out node);
            }

            if (node is null)
            {
                StateItemsPriority2.TryPeek(out node);
            }

            return node;
        }

        private float CalculatePriority(NodeDataType nodeDataType, byte level, uint rightness)
        {
            if (nodeDataType == NodeDataType.Code)
            {
                return 0f;
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

            return priority;
        }

        private float CalculatePriority(byte level, uint rightness)
        {
            // the more synced we are the more to the right we want to go - hence the sync progress modifier at the end
            // we want to keep more or less to the same side (left or right - we chose left) so we punish
            // the high child indices
            // we want to go deep first so we add bonus for the depth
            float priority = 1.00f - (float)level / Math.Max(MaxStateLevel, (byte)1) + (float)rightness / Math.Max(_maxRightness, 1) - (float)_lastSyncProgress / 2;
            return priority;
        }

        public void Clear()
        {
            for (int i = 0; i < _allStacks.Length; i++)
            {
                _allStacks[i]?.Clear();
            }
        }

        /// <summary>
        /// It takes items only from state node queues.
        /// Codes are taken from the queue separately to support SNAP methods (2 different methods)
        /// </summary>
        /// <param name="node"></param>
        /// <returns></returns>
        private bool TryTake(out StateSyncItem? node)
        {
            // index 0 is Codes so we skip it
            for (int i = 1; i < _allStacks.Length; i++)
            {
                if (_allStacks[i].TryPop(out node))
                {
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
            int length = MaxStateLevel == 64 ? maxSize : Math.Max(1, (int)(maxSize * ((decimal)MaxStateLevel / 64) * ((decimal)MaxStateLevel / 64)));
            List<StateSyncItem> requestItems = new(length);

            // Codes have priority over State Nodes
            if (!CodeItems.IsEmpty)
            {
                int codeMaxCount = Math.Min(length, CodeItems.Count);

                for (int i = 0; i < codeMaxCount; i++)
                {
                    if (CodeItems.TryPop(out var codeItem))
                    {
                        requestItems.Add(codeItem!);
                    }
                }

                if (requestItems.Count > 0)
                {
                    return requestItems;
                }
            }

            // Take Stae Nodes if no codes queued up
            for (int i = 0; i < length; i++)
            {
                if (TryTake(out StateSyncItem? requestItem))
                {
                    requestItems.Add(requestItem!);
                }
                else
                {
                    break;
                }
            }

            return requestItems;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public string RecalculatePriorities()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            string reviewMessage = $"Node sync queues review ({LevelsDescription}):" + Environment.NewLine;
            reviewMessage += $"  before {Description}" + Environment.NewLine;

            List<StateSyncItem> temp = new();
            while (StateItemsPriority2.TryPop(out StateSyncItem? poppedSyncItem))
            {
                temp.Add(poppedSyncItem!);
            }

            while (StorageItemsPriority2.TryPop(out StateSyncItem? poppedSyncItem))
            {
                temp.Add(poppedSyncItem!);
            }

            foreach (StateSyncItem syncItem in temp)
            {
                PushToSelectedStream(syncItem, _lastSyncProgress);
            }

            reviewMessage += $"  after {Description}" + Environment.NewLine;

            stopwatch.Stop();
            reviewMessage += $"  time spent in review: {stopwatch.ElapsedMilliseconds}ms";
            return reviewMessage;
        }
    }
}
