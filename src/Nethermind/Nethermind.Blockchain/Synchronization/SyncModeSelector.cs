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
using Nethermind.Core.Specs;

namespace Nethermind.Blockchain.Synchronization
{
    internal class SyncModeSelector
    {
        public const int FullSyncThreshold = 32;

        private readonly ISyncProgressResolver _syncProgressResolver;
        private readonly IEthSyncPeerPool _syncPeerPool;
        private readonly ISpecProvider _specProvider;
        private readonly ILogger _logger;

        private bool _fastSyncEnabled;

        public SyncModeSelector(ISyncProgressResolver syncProgressResolver, IEthSyncPeerPool syncPeerPool, ISyncConfig syncConfig, ISpecProvider specProvider, ILogManager logManager)
        {
            _syncProgressResolver = syncProgressResolver ?? throw new ArgumentNullException(nameof(syncProgressResolver));
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            _fastSyncEnabled = syncConfig?.FastSync ?? false;
            Current = _fastSyncEnabled ? SyncMode.Headers : SyncMode.Full;
        }

        public SyncMode Current { get; private set; }
        
        public void Update()
        {
            if (!_fastSyncEnabled)
            {
                return;
            }
            
            if (_syncPeerPool.PeerCount == 0)
            {
                return;
            }

            long bestFullBlock = _syncProgressResolver.FindBestFullBlock();
            long bestHeader = _syncProgressResolver.FindBestHeader();
            long bestFullState = _syncProgressResolver.FindBestFullState();
            long lowestInserted = _syncProgressResolver.FindLowestInserted();
            if (bestFullBlock < 0 || bestHeader < 0 || bestFullState < 0  || bestFullBlock > bestHeader)
            {
                string errorMessage = $"Invalid best state calculation: F:{bestFullBlock}|H:{bestHeader}|S:{bestFullState}";
                if(_logger.IsError) _logger.Error(errorMessage);
                throw new InvalidOperationException(errorMessage);
            }

            
            long maxBlockNumberAmongPeers = 0;
            foreach (PeerInfo peerInfo in _syncPeerPool.UsefulPeers)
            {
                maxBlockNumberAmongPeers = Math.Max(maxBlockNumberAmongPeers, peerInfo.HeadNumber);
            }

            SyncMode newSyncMode;
            if (bestHeader <= _specProvider.PivotBlockNumber)
            {
                newSyncMode = SyncMode.AncientBlocks;
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
                if (_logger.IsInfo) _logger.Info($"Switching sync mode from {Current} to {newSyncMode} best_header:{bestHeader}|best_full:{bestFullBlock}|best_state:{bestFullState}|best_heard_of:{maxBlockNumberAmongPeers}|lowest_inserted{lowestInserted}.");
                Current = newSyncMode;
                Changed?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                if (_logger.IsInfo) _logger.Info($"Staying on sync mode {Current} |best header:{bestHeader}|best full block:{bestFullBlock}|best state:{bestFullState}|best peer block:{maxBlockNumberAmongPeers}");
            }
        }

        public event EventHandler Changed;
    }
}