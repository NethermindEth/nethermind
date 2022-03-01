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
using System.Text;
using Nethermind.Logging;

namespace Nethermind.Synchronization.FastSync
{
    /// <summary>
    /// This class represents sync progress as a percentage of second level branches saved in the database.
    /// It generates a log view like below.
    /// 2021-01-17 00:14:11.4390|Branch sync progress (do not extrapolate): 16.02% of block 4117567
    /// **********************+++++++++**++++++++**********+************
    /// ****************************************************************
    /// ++++++++++++++++++*+++++****************************************
    /// **************************************************************** 
    /// </summary>
    internal class BranchProgress
    {
        private ILogger _logger;
        private NodeProgressState[] _syncProgress;
        
        public decimal LastProgress { get; private set; }
        public long CurrentSyncBlock { get; }

        public BranchProgress(long syncBlockNumber, ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            CurrentSyncBlock = syncBlockNumber;
            _logger.Info($"Now syncing nodes starting from root of block {syncBlockNumber}");
            _syncProgress = new NodeProgressState[256];
        }
        
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

        public void ReportSynced(StateSyncItem syncItem, NodeProgressState nodeProgressState)
        {
            ReportSynced(
                syncItem.Level,
                syncItem.ParentBranchChildIndex,
                syncItem.BranchChildIndex,
                syncItem.NodeDataType,
                NodeProgressState.Requested);
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
                if (_syncProgress[i] != NodeProgressState.Unknown && _syncProgress[i] != NodeProgressState.Requested)
                {
                    savedBranches += 1;
                }
            }

            decimal currentProgress = (decimal) savedBranches / _syncProgress.Length;

            if (currentProgress == LastProgress)
            {
                return;
            }

            Metrics.StateBranchProgress = (int) (currentProgress * 100);
            LastProgress = currentProgress;
            if (nodeProgressState == NodeProgressState.Empty)
            {
                return;
            }

            string detailsString = string.Empty;
            if (_logger.IsInfo)
            {
                StringBuilder builder = new();
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

                detailsString = builder.ToString();
            }

            Progress = (decimal)savedBranches / _syncProgress.Length;
            if (_logger.IsInfo) _logger.Info($"Branch sync progress (do not extrapolate): {Progress:p2} of block {CurrentSyncBlock}{detailsString}");
        }

        public decimal Progress { get; set; }
    }
}
