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

using System;
using Nethermind.Logging;

namespace Nethermind.Blockchain.Synchronization
{
    public class SyncModeSelector : ISyncModeSelector
    {
        public const int FullSyncThreshold = 32;
        
        private readonly StayingOnSyncMode _stayingOnSyncMode = new StayingOnSyncMode(StayingOnSyncMode.SyncDelayMode.AllSynced);

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

        public void Update()
        {
            if (_syncPeerPool.PeerCount == 0)
            {
                return;
            }

            if (_syncConfig.BeamSyncEnabled)
            {
                if (Current != SyncMode.Beam)
                {
                    ChangeSyncMode(SyncMode.Beam);
                }

                return;
            }

            if (!_syncConfig.FastSync)
            {
                if (Current == SyncMode.NotStarted)
                {
                    ChangeSyncMode(SyncMode.Full);
                }

                return;
            }

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

            if (maxBlockNumberAmongPeers == 0)
            {
                return;
            }
            // if (maxBlockNumberAmongPeers <= FullSyncThreshold)
            // {
            //     return;
            // }

            SyncMode newSyncMode;
            long bestFull = Math.Max(bestFullState, bestFullBlock);
            if (!_syncProgressResolver.IsFastBlocksFinished())
            {
                newSyncMode = SyncMode.FastBlocks;
            }
            else if (maxBlockNumberAmongPeers - bestFull <= FullSyncThreshold)
            {
                if (maxBlockNumberAmongPeers < bestFull)
                {
                    return;
                }

                newSyncMode = bestFull >= bestHeader ? SyncMode.Full : SyncMode.StateNodes;
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

            _stayingOnSyncMode.SetChainInfo(bestHeader, bestFullBlock, bestFullState);
            
            if (newSyncMode != Current)
            {
                if (_logger.IsInfo) _logger.Info($"Switching sync mode from {Current} to {newSyncMode} {BuildStateString(bestHeader, bestFullBlock, bestFullState, maxBlockNumberAmongPeers)}");
                _stayingOnSyncMode.Notify();
                ChangeSyncMode(newSyncMode);
            }
            else 
            {
                if (_stayingOnSyncMode.ShouldLog())
                {
                    if (_logger.IsInfo) _logger.Info($"Staying on sync mode {Current} {BuildStateString(bestHeader, bestFullBlock, bestFullState, maxBlockNumberAmongPeers)}");
                    _stayingOnSyncMode.Notify();
                }
            }
        }

        private void ChangeSyncMode(SyncMode newSyncMode)
        {
            SyncMode previous = Current;
            Current = newSyncMode;
            Changed?.Invoke(this, new SyncModeChangedEventArgs(previous, Current));
        }

        private string BuildStateString(long bestHeader, long bestFullBlock, long bestFullState, long bestAmongPeers) =>
            $"|best header:{bestHeader}|best full block:{bestFullBlock}|best state:{bestFullState}|best peer block:{bestAmongPeers}";

        public event EventHandler<SyncModeChangedEventArgs> Changed;

        private class StayingOnSyncMode
        {
            private readonly SyncDelayMode _mode;
            private TimeSpan NoSyncDelay { get; } = TimeSpan.FromSeconds(3);
            private TimeSpan MaxSyncDelay { get; } = TimeSpan.FromSeconds(20);
            private DateTime LastNotification { get; set; } = DateTime.UtcNow;
            private DateTime LastChainInfo { get; set; } = DateTime.UtcNow;
            private long BestFullBlock { get; set; }
            private long BestHeader { get; set; }
            private long BestFullState { get; set; }

            public StayingOnSyncMode(SyncDelayMode mode)
            {
                _mode = mode;
            }

            public void Notify()
            {
                LastNotification = DateTime.UtcNow;
            }

            public void SetChainInfo(in long bestHeader, in long bestFullBlock, in long bestFullState)
            {
                var synced = _mode switch
                {
                    SyncDelayMode.AnythingSynced => (BestHeader != bestHeader || BestFullBlock != bestFullBlock || BestFullState != bestFullState),
                    SyncDelayMode.AllSynced => (BestHeader != bestHeader && BestFullBlock != bestFullBlock && BestFullState != bestFullState),
                    _ => false
                };
                
                if (synced)
                {
                    BestHeader = bestHeader;
                    BestFullBlock = bestFullBlock;
                    BestFullState = bestFullState;
                    LastChainInfo = DateTime.UtcNow;
                }
            }

            public bool ShouldLog()
            {
                bool CheckDelay(DateTime lastDate, TimeSpan delay)
                {
                    return DateTime.UtcNow - lastDate >= delay;
                }

                return CheckDelay(LastChainInfo, MaxSyncDelay) && CheckDelay(LastNotification, NoSyncDelay);
            }
            
            public enum SyncDelayMode
            {
                AnythingSynced,
                AllSynced
            }
        }
    }
}