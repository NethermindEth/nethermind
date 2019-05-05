/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Text;
using Nethermind.Core.Logging;

namespace Nethermind.Blockchain.Synchronization
{
    internal class NodeSyncProgress
    {
        public NodeSyncProgress(long syncBlockNumber, ILogger logger)
        {
            CurrentSyncBlock = syncBlockNumber;
            _logger = logger;
            _syncProgress = new NodeProgressState[256];
        }

        public long CurrentSyncBlock { get; }

        private readonly ILogger _logger;

        private DateTime _lastProgressTime = DateTime.MinValue;

        private NodeProgressState[] _syncProgress;
        public decimal LastProgress { get; private set; }

        private void ReportSyncedLevel1(int childIndex, NodeProgressState nodeProgressState)
        {
            if (childIndex == -1)
            {
                for (int i = 0; i < 256; i++)
                {
                    UpdateState(i, nodeProgressState);
                }
            }
            else
            {
                for (int i = 16 * childIndex; i < 16 * childIndex + 16; i++)
                {
                    UpdateState(i, nodeProgressState);
                }
            }
        }

        void UpdateState(int index, NodeProgressState newState)
        {
            if (_syncProgress[index] == NodeProgressState.Unknown || _syncProgress[index] == NodeProgressState.Requested)
            {
                _syncProgress[index] = newState;
            }
        }
        
        private void ReportSyncedLevel2(int parentIndex, int childIndex, NodeProgressState nodeProgressState)
        {
            if (parentIndex == -1)
            {
                ReportSyncedLevel1(childIndex, nodeProgressState);
                return;
            }

            if (childIndex == -1)
            {
                for (int i = 0; i < 16; i++)
                {
                    UpdateState(16 * parentIndex + i, nodeProgressState);
                }
            }
            else
            {
                UpdateState(16 * parentIndex + childIndex, nodeProgressState);
            }
        }

        public void ReportSynced(int level, int parentIndex, int childIndex, NodeDataType nodeDataType, NodeProgressState nodeProgressState)
        {
            if (level > 2 || nodeDataType != NodeDataType.State)
            {
                return;
            }

            switch (level)
            {
                case 0:
                    for (int i = 0; i < 256; i++)
                    {
                        UpdateState(i, nodeProgressState);
                    }

                    break;
                case 1:
                    ReportSyncedLevel1(childIndex, nodeProgressState);
                    break;
                case 2:
                    ReportSyncedLevel2(parentIndex, childIndex, nodeProgressState);
                    break;
            }

            int savedBranches = 0;
            for (int i = 0; i < _syncProgress.Length; i++)
            {
                if (_syncProgress[i] != NodeProgressState.Unknown)
                {
                    savedBranches += 1;
                }
            }

            decimal currentProgress = (decimal) savedBranches / _syncProgress.Length;
            if (currentProgress == LastProgress)
            {
                return;
            }

            LastProgress = currentProgress;
            if (nodeProgressState == NodeProgressState.Empty)
            {
                return;
            }

            if (_logger.IsInfo) _logger.Info($"Node sync progress: {(decimal) savedBranches / _syncProgress.Length:p2} from block {CurrentSyncBlock}");

//                if (currentProgress != 1M && DateTime.UtcNow - _lastProgressTime < TimeSpan.FromSeconds(5))
//                {
//                    return;
//                }

            _lastProgressTime = DateTime.UtcNow;
            if (_logger.IsInfo)
            {
                StringBuilder builder = new StringBuilder();
//                    builder.Append($"(after {level} {parentIndex}.{childIndex} {nodeProgressState})");
                for (int i = 0; i < _syncProgress.Length; i++)
                {
                    if (i % 64 == 0)
                    {
                        builder.AppendLine();
                    }

                    switch (_syncProgress[i])
                    {
                        case NodeProgressState.Unknown:
                            builder.Append('?');
                            break;
                        case NodeProgressState.Empty:
                            builder.Append('0');
                            break;
                        case NodeProgressState.AlreadySaved:
                            builder.Append('1');
                            break;
                        case NodeProgressState.Saved:
                            builder.Append('+');
                            break;
                        case NodeProgressState.Requested:
                            builder.Append('*');
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }

                _logger.Info(builder.ToString());
            }
        }
    }

    public enum NodeProgressState
    {
        Unknown,
        Empty,
        Requested,
        AlreadySaved,
        Saved
    }
}