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
using System.Linq;
using System.Timers;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.ParallelSync
{
    public class MultiSyncModeSelector : ISyncModeSelector, IDisposable
    {
        /// <summary>
        /// Number of blocks before the best peer's head when we switch from fast sync to full sync
        /// </summary>
        private const int FullSyncThreshold = 32;

        private readonly ISyncProgressResolver _syncProgressResolver;
        private readonly ISyncPeerPool _syncPeerPool;
        private readonly ISyncConfig _syncConfig;
        private readonly ILogger _logger;

        private bool HasPeers => _syncPeerPool.UsefulPeers.Any();
        private bool BeamSyncEnabled => _syncConfig.BeamSync;
        private bool FastSyncEnabled => _syncConfig.FastSync;
        private bool FastBlocksEnabled => _syncConfig.FastSync && _syncConfig.FastBlocks;
        private bool FastBlocksFinished => !FastBlocksEnabled || _syncProgressResolver.IsFastBlocksFinished();
        private long FastSyncCatchUpHeightDelta => _syncConfig.FastSyncCatchUpHeightDelta ?? FullSyncThreshold;

        private long MaxPeerBlockNumber
        {
            get
            {
                long maxPeerBlock = 0;
                foreach (PeerInfo usefulPeer in _syncPeerPool.UsefulPeers)
                {
                    maxPeerBlock = Math.Max(maxPeerBlock, usefulPeer.HeadNumber);
                }

                return maxPeerBlock;
            }
        }

        private System.Timers.Timer _timer;

        public MultiSyncModeSelector(ISyncProgressResolver syncProgressResolver, ISyncPeerPool syncPeerPool, ISyncConfig syncConfig, ILogManager logManager)
        {
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _syncProgressResolver = syncProgressResolver ?? throw new ArgumentNullException(nameof(syncProgressResolver));

            if (syncConfig.FastSyncCatchUpHeightDelta <= FullSyncThreshold)
            {
                if (_logger.IsWarn) _logger.Warn($"'FastSyncCatchUpHeightDelta' parameter is less or equal to {FullSyncThreshold}, which is a threshold of blocks always downloaded in full sync. 'FastSyncCatchUpHeightDelta' will have no effect.");
            }

            StartUpdateTimer();
        }

        private void StartUpdateTimer()
        {
            _timer = new System.Timers.Timer();
            _timer.Interval = 1000;
            _timer.AutoReset = false;
            _timer.Elapsed += TimerOnElapsed;
            _timer.Enabled = true;
        }

        private void TimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                Update();
            }
            catch (Exception exception)
            {
                if (_logger.IsError) _logger.Error("Sync mode update failed", exception);
            }

            _timer.Enabled = true;
        }

        public SyncMode Current { get; private set; } = SyncMode.None;

        private bool IsInAStickyFullSyncMode(Snapshot best)
        {
            bool hasEverBeenInFullSync = best.Processed > 0;
            long heightDelta = best.PeerBlock - best.Header;
            return hasEverBeenInFullSync && heightDelta < FastSyncCatchUpHeightDelta;
        }

        private bool IsWaitingForBlockProcessor(Snapshot best)
        {
            return best.Block > best.State && best.State > 0;
        }

        private bool ShouldBeInFastSyncMode(Snapshot best)
        {
            if (!FastSyncEnabled)
            {
                return false;
            }

            if (!_syncProgressResolver.IsFastBlocksFinished() && best.Header == 0)
            {
                // do not start fast sync until at least one header is downloaded or we would start from zero
                // we are fine to start from zero if we do not use fast blocks
                return false;
            }

            long heightDelta = best.PeerBlock - best.Header;
            return
                // (catch up after node is off for a while
                // OR standard fast sync)
                !IsInAStickyFullSyncMode(best)
                && heightDelta > FullSyncThreshold
                // AND not waiting for processor
                && !IsWaitingForBlockProcessor(best);
        }

        private bool ShouldBeInFullSyncMode(Snapshot best)
        {
            // it can be still in fast blocks but not any other sync mode
            return !ShouldBeInBeamSyncMode(best) &&
                   !ShouldBeInFastSyncMode(best) &&
                   !ShouldBeInStateNodesMode(best);
        }

        private bool ShouldBeInFastBlocksMode(Snapshot best)
        {
            // this is really the only condition - fast blocks can always run if there are peers until it is done
            // also fast blocks can run in parallel with all other sync modes
            return FastBlocksEnabled && !FastBlocksFinished;
        }

        private bool ShouldBeInStateNodesMode(Snapshot best)
        {
            return FastSyncEnabled
                   // not downloading headers and bodies any more
                   && !ShouldBeInFastSyncMode(best)
                   // state is not yet downloaded
                   && (best.PeerBlock - best.State > FullSyncThreshold
                       // headers went too far
                       || best.Header > best.State)
                   // full sync is not in progress
                   && !IsWaitingForBlockProcessor(best)
                   && !IsInAStickyFullSyncMode(best);
        }

        private bool ShouldBeInBeamSyncMode(Snapshot best)
        {
            return BeamSyncEnabled && !ShouldBeInFastBlocksMode(best);
        }

        public void Update()
        {
            if (_syncProgressResolver.IsLoadingBlocksFromDb())
            {
                UpdateSyncModes(SyncMode.DbLoad);
                return;
            }
            
            bool hasPeers = HasPeers;
            long peerBlock = MaxPeerBlockNumber;

            // if there are no peers that we could use then we cannot sync
            if (!hasPeers || peerBlock == 0)
            {
                UpdateSyncModes(SyncMode.None);
                return;
            }

            // to avoid expensive checks we make this simple check at the beginning
            if (!FastSyncEnabled && !BeamSyncEnabled)
            {
                UpdateSyncModes(SyncMode.Full);
                return;
            }

            Snapshot best = TakeSnapshot(peerBlock);

            SyncMode newModes = SyncMode.None;
            if (ShouldBeInBeamSyncMode(best))
            {
                newModes |= SyncMode.Beam;
            }

            if (ShouldBeInFastBlocksMode(best))
            {
                newModes |= SyncMode.FastBlocks;
            }

            if (ShouldBeInFastSyncMode(best))
            {
                newModes |= SyncMode.FastSync;
            }

            if (ShouldBeInFullSyncMode(best))
            {
                newModes |= SyncMode.Full;
            }

            if (ShouldBeInStateNodesMode(best))
            {
                newModes |= SyncMode.StateNodes;
            }

            if (newModes != Current)
            {
                string stateString = BuildStateString(best);
                string message = $"Changing state to {newModes} at {stateString}";
                if (_logger.IsInfo) _logger.Info(message);
            }

            UpdateSyncModes(newModes);
        }

        private Snapshot TakeSnapshot(long peerBlock)
        {
            long header = _syncProgressResolver.FindBestHeader();
            long block = _syncProgressResolver.FindBestFullBlock();
            long state = _syncProgressResolver.FindBestFullState();
            long processed = _syncProgressResolver.FindBestProcessedBlock();

            Snapshot best = new Snapshot(processed, state, block, header, peerBlock);
            VerifySnapshot(best);
            return best;
        }

        private void VerifySnapshot(Snapshot best)
        {
            if ( // none of these values should ever be negative
                best.Block < 0
                || best.Header < 0
                || best.State < 0
                || best.Processed < 0
                || best.PeerBlock < 0
                // best header is at least equal to the best full block
                || best.Block > best.Header
                // we cannot download state for an unknown header
                || best.State > best.Header
                // we can only process blocks for which we have full body
                || best.Processed > best.Block
                // for any processed block we should have its full state   
                || best.Processed > best.State)
            {
                string stateString = BuildStateString(best);
                string errorMessage = $"Invalid best state calculation: {stateString}";
                if (_logger.IsError) _logger.Error(errorMessage);
                throw new InvalidOperationException(errorMessage);
            }
        }

        private void UpdateSyncModes(SyncMode newModes)
        {
            // if (Current == newModes)
            // {
            //     return;
            // }

            SyncMode previous = Current;
            Current = newModes;
            Changed?.Invoke(this, new SyncModeChangedEventArgs(previous, Current));
        }

        /// <summary>
        /// We display the state in the most likely ascending order
        /// </summary>
        /// <param name="best">Snapshot of the best known states</param>
        /// <returns>A string describing the state of sync</returns>
        private static string BuildStateString(Snapshot best) =>
            $"processed:{best.Processed}|state:{best.State}|block:{best.Block}|header:{best.Header}|peer block:{best.PeerBlock}";

        public event EventHandler<SyncModeChangedEventArgs> Changed;

        private struct Snapshot
        {
            public Snapshot(long processed, long state, long block, long header, long peerBlock)
            {
                Processed = processed;
                State = state;
                Block = block;
                Header = header;
                PeerBlock = peerBlock;
            }

            /// <summary>
            /// Best block that has been processed
            /// </summary>
            public long Processed { get; }

            /// <summary>
            /// Best full block state in the state trie (may not be processed if we just finished state trie download)
            /// </summary>
            public long State { get; }

            /// <summary>
            /// Best block body
            /// </summary>
            public long Block { get; }

            /// <summary>
            /// Best block header - may be missing body if we just insert headers
            /// </summary>
            public long Header { get; }

            /// <summary>
            /// Best peer block - this is what other peers are advertising - it may be lower than our best block if we get disconnected from best peers
            /// </summary>
            public long PeerBlock { get; }
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}