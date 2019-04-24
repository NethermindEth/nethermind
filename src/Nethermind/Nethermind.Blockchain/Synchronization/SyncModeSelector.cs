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
using Nethermind.Core.Logging;
using Nethermind.Store;

namespace Nethermind.Blockchain.Synchronization
{
    internal class SyncModeSelector
    {
        public const int FullSyncThreshold = 32;
        
        private readonly IEthSyncPeerPool _syncPeerPool;
        private readonly ILogger _logger;

        private bool _fastSyncEnabled;

        public SyncModeSelector(IEthSyncPeerPool syncPeerPool, ISyncConfig syncConfig, ILogManager logManager)
        {
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            _fastSyncEnabled = syncConfig?.FastSync ?? false;
            Current = _fastSyncEnabled ? SyncMode.Headers : SyncMode.Full;
        }

        public SyncMode Current { get; private set; }

        public void Update(long bestHeader, long bestFullState)
        {
            if (!_fastSyncEnabled)
            {
                return;
            }
            
            if (_syncPeerPool.PeerCount == 0)
            {
                return;
            }

            long maxBlockNumberAmongPeers = 0;
            foreach (PeerInfo peerInfo in _syncPeerPool.AllPeers)
            {
                maxBlockNumberAmongPeers = Math.Max(maxBlockNumberAmongPeers, peerInfo.HeadNumber);
            }

            SyncMode newSyncMode;
            if (maxBlockNumberAmongPeers - bestFullState <= FullSyncThreshold)
            {
                newSyncMode = SyncMode.Full;
            }
            else if (maxBlockNumberAmongPeers - bestHeader <= FullSyncThreshold)
            {
                newSyncMode = SyncMode.StateNodes;
            }
            else
            {
                newSyncMode = SyncMode.Headers;
            }

            if (newSyncMode != Current)
            {
                if (_logger.IsInfo) _logger.Info($"Switching sync mode from {Current} to {newSyncMode} {bestHeader}|{bestFullState}|{maxBlockNumberAmongPeers}.");
                Current = newSyncMode;
                Changed?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                if (_logger.IsInfo) _logger.Info($"Staying on sync mode {Current} {bestHeader}|{bestFullState}|{maxBlockNumberAmongPeers}.");
            }
        }

        public event EventHandler Changed;
    }
}