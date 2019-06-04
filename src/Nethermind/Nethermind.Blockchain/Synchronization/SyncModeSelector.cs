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
using Nethermind.Logging;
using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Core.Json;
using Nethermind.Core.Specs;

namespace Nethermind.Blockchain.Synchronization
{
    internal class SyncModeSelector
    {
        public const int FullSyncThreshold = 32;

        private readonly ISyncProgressResolver _syncProgressResolver;
        private readonly IEthSyncPeerPool _syncPeerPool;
        private readonly ISyncConfig _syncConfig;
        private readonly ILogger _logger;

        public SyncModeSelector(ISyncProgressResolver syncProgressResolver, IEthSyncPeerPool syncPeerPool, ISyncConfig syncConfig, ILogManager logManager)
        {
            _syncProgressResolver = syncProgressResolver ?? throw new ArgumentNullException(nameof(syncProgressResolver));
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            
            Current = SyncMode.NotStarted;
        }

        public SyncMode Current { get; private set; }

        public bool IsParallel => Current == SyncMode.FastBlocks || Current == SyncMode.StateNodes;
        
        public void Update()
        {
            if (_syncPeerPool.PeerCount == 0)
            {
                return;
            }

            if (!_syncConfig.FastSync)
            {
                if (Current == SyncMode.NotStarted)
                {
                    Current = SyncMode.Full;
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
            else
            {
                long bestHeader = _syncProgressResolver.FindBestHeader();
                long bestFullBlock = _syncProgressResolver.FindBestFullBlock();
                long bestFullState = _syncProgressResolver.FindBestFullState();
                long maxBlockNumberAmongPeers = 0;
                if (bestFullBlock < 0
                    || bestHeader < 0
                    || bestFullState < 0
                    || bestFullBlock > bestHeader)
                {
                    string errorMessage = $"Invalid best state calculation: {BuildStateString(bestHeader, bestFullBlock, bestFullBlock, maxBlockNumberAmongPeers)}";
                    if (_logger.IsError) _logger.Error(errorMessage);
                    throw new InvalidOperationException(errorMessage);
                }

                foreach (PeerInfo peerInfo in _syncPeerPool.UsefulPeers)
                {
                    maxBlockNumberAmongPeers = Math.Max(maxBlockNumberAmongPeers, peerInfo.HeadNumber);
                }

                SyncMode newSyncMode;
                if (!_syncProgressResolver.IsFastBlocksFinished())
                {
                    newSyncMode = SyncMode.FastBlocks;
                }
                else if (maxBlockNumberAmongPeers - Math.Max(bestFullState, bestFullBlock) <= FullSyncThreshold)
                {
                    newSyncMode = Math.Max(bestFullState, bestFullBlock) >= bestHeader ? SyncMode.Full : SyncMode.StateNodes;
                }
                else if (maxBlockNumberAmongPeers - bestHeader <= FullSyncThreshold)
                {
                    // TODO: we need to check here if there are any blocks in processing queue... any other checks are wrong
                    newSyncMode = bestFullBlock > bestFullState ? SyncMode.WaitForProcessor : SyncMode.StateNodes;
                }
                else
                {
                    newSyncMode = bestFullBlock > bestFullState ? SyncMode.WaitForProcessor : SyncMode.Headers;
                }
                
                if (newSyncMode != Current)
                {
                    if (_logger.IsInfo) _logger.Info($"Switching sync mode from {Current} to {newSyncMode} {BuildStateString(bestHeader, bestFullBlock, bestFullState, maxBlockNumberAmongPeers)}");
                    Current = newSyncMode;
                    Changed?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    if (_logger.IsInfo) _logger.Info($"Staying on sync mode {Current} {BuildStateString(bestHeader, bestFullBlock, bestFullState, maxBlockNumberAmongPeers)}");
                }
            }
        }

        private string BuildStateString(long bestHeader, long bestFullBlock, long bestFullState, long bestAmongPeers)
        {
            return $"|best header:{bestHeader}|best full block:{bestFullBlock}|best state:{bestFullState}|best peer block:{bestAmongPeers}";
        }
        
        public event EventHandler Changed;
    }
}