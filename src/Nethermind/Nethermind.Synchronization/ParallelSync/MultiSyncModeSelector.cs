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
using System.ComponentModel;
using System.Timers;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.ParallelSync
{
    public class MultiSyncModeSelector : ISyncModeSelector, IDisposable
    {
        /// <summary>
        /// Number of blocks before the best peer's head when we switch from fast sync to full sync
        /// </summary>
        public const int FastSyncLag = 32;
        
        /// <summary>
        /// How many blocks can fast sync stay behind while state nodes is still syncing
        /// </summary>
        public const int StickyStateNodesDelta = 32;

        private readonly ISyncProgressResolver _syncProgressResolver;
        private readonly ISyncPeerPool _syncPeerPool;
        private readonly ISyncConfig _syncConfig;
        private readonly ILogger _logger;

        private long PivotNumber;
        private bool BeamSyncEnabled => _syncConfig.BeamSync;
        private bool FastSyncEnabled => _syncConfig.FastSync;
        private bool FastBlocksEnabled => _syncConfig.FastSync && _syncConfig.FastBlocks;
        private bool FastBlocksFinished => !FastBlocksEnabled || _syncProgressResolver.IsFastBlocksFinished();
        private long FastSyncCatchUpHeightDelta => _syncConfig.FastSyncCatchUpHeightDelta ?? FastSyncLag;

        private System.Timers.Timer _timer;

        public MultiSyncModeSelector(ISyncProgressResolver syncProgressResolver, ISyncPeerPool syncPeerPool, ISyncConfig syncConfig, ILogManager logManager)
        {
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _syncProgressResolver = syncProgressResolver ?? throw new ArgumentNullException(nameof(syncProgressResolver));

            if (syncConfig.FastSyncCatchUpHeightDelta <= FastSyncLag)
            {
                if (_logger.IsWarn) _logger.Warn($"'FastSyncCatchUpHeightDelta' parameter is less or equal to {FastSyncLag}, which is a threshold of blocks always downloaded in full sync. 'FastSyncCatchUpHeightDelta' will have no effect.");
            }

            PivotNumber = _syncConfig.PivotNumberParsed;

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

        public void DisableTimer()
        {
            // for testing
            _timer.Stop();
        }

        public void Update()
        {
            if (_syncProgressResolver.IsLoadingBlocksFromDb())
            {
                UpdateSyncModes(SyncMode.DbLoad);
                return;
            }

            if (!_syncConfig.SynchronizationEnabled)
            {
                UpdateSyncModes(SyncMode.None);
                return;
            }

            (UInt256? peerDifficulty, long? peerBlock) = ReloadDataFromPeers();

            // if there are no peers that we could use then we cannot sync
            if (peerDifficulty == null || peerBlock == null || peerBlock == 0)
            {
                UpdateSyncModes(SyncMode.None);
                return;
            }

            // to avoid expensive checks we make this simple check at the beginning
            if (!FastSyncEnabled)
            {
                bool anyPeers = peerBlock.Value > 0 && peerDifficulty.Value > _syncProgressResolver.ChainDifficulty;
                UpdateSyncModes(anyPeers ? SyncMode.Full : SyncMode.None);
                return;
            }

            Snapshot best;
            try
            {
                best = TakeSnapshot(peerDifficulty.Value, peerBlock.Value);
            }
            catch (InvalidAsynchronousStateException)
            {
                UpdateSyncModes(SyncMode.None);
                return;
            }

            best.IsInFastBlocks = ShouldBeInFastBlocksMode(best);
            best.IsInFastSync = ShouldBeInFastSyncMode(best);
            best.IsInStateSync = ShouldBeInStateNodesMode(best);
            best.IsInBeamSync = ShouldBeInBeamSyncMode(best);
            best.IsInFullSync = ShouldBeInFullSyncMode(best);

            SyncMode newModes = SyncMode.None;
            if (best.IsInBeamSync)
            {
                newModes |= SyncMode.Beam;
            }

            if (best.IsInFastBlocks)
            {
                newModes |= SyncMode.FastBlocks;
            }

            if (best.IsInFastSync)
            {
                newModes |= SyncMode.FastSync;
            }

            if (best.IsInFullSync)
            {
                newModes |= SyncMode.Full;
            }

            if (best.IsInStateSync)
            {
                newModes |= SyncMode.StateNodes;
            }
            
            if (IsTheModeSwitchWorthMentioning(newModes))
            {
                string stateString = BuildStateString(best);
                string message = $"Changing state to {newModes} at {stateString}";
                if (_logger.IsInfo) _logger.Info(message);
            }

            UpdateSyncModes(newModes);
        }

        private bool IsTheModeSwitchWorthMentioning(SyncMode newModes)
        {
            return _logger.IsDebug ||
                   newModes != Current &&
                   (newModes != SyncMode.None || Current != SyncMode.Full) &&
                   (newModes != SyncMode.Full || Current != SyncMode.None);
        }

        private void UpdateSyncModes(SyncMode newModes)
        {
            if (_logger.IsTrace)
            {
                if (newModes != Current)
                {
                    string message = $"Changing state to {newModes}";
                    if (_logger.IsTrace) _logger.Trace(message);
                }
            }

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
            $"processed:{best.Processed}|beam state:{best.BeamState}|state:{best.State}|block:{best.Block}|header:{best.Header}|peer block:{best.PeerBlock}";

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
            bool hasEverBeenInFullSync = best.Processed > PivotNumber && best.State > PivotNumber;
            long heightDelta = best.PeerBlock - best.Header;
            return hasEverBeenInFullSync && heightDelta < FastSyncCatchUpHeightDelta;
        }

        private bool ShouldBeInFastSyncMode(Snapshot best)
        {
            if (!FastSyncEnabled)
            {
                return false;
            }

            if (_syncConfig.FastBlocks && PivotNumber != 0 && best.Header == 0)
            {
                // do not start fast sync until at least one header is downloaded or we would start from zero
                // we are fine to start from zero if we do not use fast blocks
                return false;
            }

            long heightDelta = best.PeerBlock - best.Header;
            bool heightDeltaGreaterThanLag = heightDelta > FastSyncLag;
            bool postPivotPeerAvailable = AnyPostPivotPeerKnown(best.PeerBlock);
            bool notInAStickyFullSync = !IsInAStickyFullSyncMode(best);
            bool notHasJustStartedFullSync = !HasJustStartedFullSync(best);

            if (_logger.IsTrace)
            {
                _logger.Trace("======================== FAST");
                _logger.Trace("postPivotPeerAvailable " + postPivotPeerAvailable);
                _logger.Trace("heightDeltaGreaterThanLag " + heightDeltaGreaterThanLag);
                _logger.Trace("notInAStickyFullSync " + notInAStickyFullSync);
            }

            return
                postPivotPeerAvailable &&
                // (catch up after node is off for a while
                // OR standard fast sync)
                notInAStickyFullSync &&
                heightDeltaGreaterThanLag &&
                notHasJustStartedFullSync;
        }

        private bool ShouldBeInFullSyncMode(Snapshot best)
        {
            bool higherDiffPeerKnown = AnyPeerWithHigherDifficultyKnown(best.PeerDifficulty);
            bool postPivotPeerAvailable = AnyPostPivotPeerKnown(best.PeerBlock);
            bool hasFastSyncBeenActive = best.Header >= PivotNumber;
            bool notInBeamSync = !best.IsInBeamSync;
            bool notInFastSync = !best.IsInFastSync;
            bool notInStateSync = !best.IsInStateSync;

            if (_logger.IsTrace)
            {
                _logger.Trace("======================== FULL");
                _logger.Trace("higherDiffPeerKnown " + higherDiffPeerKnown);
                _logger.Trace("postPivotPeerAvailable " + postPivotPeerAvailable);
                _logger.Trace("hasFastSyncBeenActive " + hasFastSyncBeenActive);
                _logger.Trace("notInBeamSync " + notInBeamSync);
                _logger.Trace("notInFastSync " + notInFastSync);
                _logger.Trace("notInStateSync " + notInStateSync);
            }

            return higherDiffPeerKnown &&
                   postPivotPeerAvailable &&
                   hasFastSyncBeenActive &&
                   notInBeamSync &&
                   notInFastSync &&
                   notInStateSync;
        }

        private bool ShouldBeInFastBlocksMode(Snapshot best)
        {
            // this is really the only condition - fast blocks can always run if there are peers until it is done
            // also fast blocks can run in parallel with all other sync modes
            return FastBlocksEnabled && !FastBlocksFinished;
        }

        private bool ShouldBeInStateNodesMode(Snapshot best)
        {
            bool fastSyncEnabled = FastSyncEnabled;
            bool fastFastSyncBeenActive = best.Header >= PivotNumber;
            bool hasAnyPostPivotPeer = AnyPostPivotPeerKnown(best.PeerBlock);
            bool notInFastSync = !best.IsInFastSync;
            bool stickyStateNodes = best.PeerBlock - best.Header < (FastSyncLag + StickyStateNodesDelta);
            bool stateNotDownloadedYet = (best.PeerBlock - best.State > FastSyncLag ||
                                          best.Header > best.State);
            bool notInAStickyFullSync = !IsInAStickyFullSyncMode(best);
            bool notHasJustStartedFullSync = !HasJustStartedFullSync(best);

            if (_logger.IsTrace)
            {
                _logger.Trace("======================== STATE");
                _logger.Trace("fastSyncEnabled " + fastSyncEnabled);
                _logger.Trace("fastFastSyncBeenActive " + fastFastSyncBeenActive);
                _logger.Trace("hasAnyPostPivotPeer " + hasAnyPostPivotPeer);
                _logger.Trace("notInFastSync " + notInFastSync);
                _logger.Trace("stateNotDownloadedYet " + stateNotDownloadedYet);
                _logger.Trace("notInAStickyFullSync " + notInAStickyFullSync);
                _logger.Trace("notHasJustStartedFullSync " + notHasJustStartedFullSync);
            }

            return fastSyncEnabled &&
                   fastFastSyncBeenActive &&
                   hasAnyPostPivotPeer &&
                   (notInFastSync || stickyStateNodes) &&
                   stateNotDownloadedYet &&
                   notHasJustStartedFullSync &&
                   notInAStickyFullSync;
        }

        private bool ShouldBeInBeamSyncMode(Snapshot best)
        {
            bool beamSyncEnabled = BeamSyncEnabled;
            bool fastSyncHasBeenActive = best.Header >= PivotNumber;
            bool hasAnyPostPivotPeer = AnyPostPivotPeerKnown(best.PeerBlock);
            bool inStateNodesSync = best.IsInStateSync;
            bool notInFastSync = !best.IsInFastSync;
            bool notInAStickyFullSync = !IsInAStickyFullSyncMode(best);
            bool notHasJustStartedFullSync = !HasJustStartedFullSync(best);

            if (_logger.IsTrace)
            {
                _logger.Trace("======================== BEAM");
                _logger.Trace("beamSyncEnabled " + beamSyncEnabled);
                _logger.Trace("fastSyncHasBeenActive " + beamSyncEnabled);
                _logger.Trace("hasAnyPostPivotPeer " + beamSyncEnabled);
                _logger.Trace("inStateNodesSync " + inStateNodesSync);
                _logger.Trace("notInFastSync " + beamSyncEnabled);
                _logger.Trace("notInAStickyFullSync " + notInAStickyFullSync);
                _logger.Trace("notHasJustStartedFullSync " + notHasJustStartedFullSync);
            }

            return beamSyncEnabled &&
                   fastSyncHasBeenActive &&
                   hasAnyPostPivotPeer &&
                   inStateNodesSync &&
                   notInAStickyFullSync &&
                   notHasJustStartedFullSync &&
                   notInFastSync;
        }

        private bool HasJustStartedFullSync(Snapshot best)
        {
            return best.State > PivotNumber && best.Processed < best.State;
        }

        private bool AnyPeerWithHigherDifficultyKnown(UInt256 bestPeerDiff)
        {
            if (_logger.IsTrace) _logger.Trace($"Is best peer diff {bestPeerDiff} > local total diff {_syncProgressResolver.ChainDifficulty}");
            return bestPeerDiff > _syncProgressResolver.ChainDifficulty;
        }

        private bool AnyPostPivotPeerKnown(long bestPeerBlock)
        {
            if (bestPeerBlock <= _syncConfig.PivotNumberParsed)
            {
                return false;
            }

            return true;
        }

        private (UInt256?, long? number) ReloadDataFromPeers()
        {
            UInt256? maxPeerDifficulty = null;
            long? number = 0;
            foreach (PeerInfo peer in _syncPeerPool.InitializedPeers)
            {
                if (peer.TotalDifficulty > (maxPeerDifficulty ?? UInt256.Zero))
                {
                    maxPeerDifficulty = peer.TotalDifficulty;
                    number = peer.HeadNumber;
                }
            }

            return (maxPeerDifficulty, number);
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }

        private Snapshot TakeSnapshot(UInt256 peerDifficulty, long peerBlock)
        {
            // need to find them in the reversed order otherwise we may fall behind the processing
            // and think that we have an invalid snapshot
            long processed = _syncProgressResolver.FindBestProcessedBlock();
            long state = _syncProgressResolver.FindBestFullState();
            long beamState = BeamSyncEnabled ? _syncProgressResolver.FindBestBeamState() : state;
            long block = _syncProgressResolver.FindBestFullBlock();
            long header = _syncProgressResolver.FindBestHeader();

            Snapshot best = new Snapshot(processed, beamState, state, block, header, peerBlock, peerDifficulty);
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
                // || (best.Processed > best.State && best.Processed > best.BeamState))
                // but we only do limited lookups for state so we need to instead fast sync to now
            )
            {
                string stateString = BuildStateString(best);
                string errorMessage = $"Invalid best state calculation: {stateString}";
                if (_logger.IsError) _logger.Error(errorMessage);
                throw new InvalidAsynchronousStateException(errorMessage);
            }
        }

        public event EventHandler<SyncModeChangedEventArgs> Changed;

        private struct Snapshot
        {
            public Snapshot(long processed, long beamState, long state, long block, long header, long peerBlock, UInt256 peerDifficulty)
            {
                Processed = processed;
                State = state;
                Block = block;
                Header = header;
                PeerBlock = peerBlock;
                PeerDifficulty = peerDifficulty;
                BeamState = beamState;

                IsInFastBlocks = IsInFastSync = IsInBeamSync = IsInFullSync = IsInStateSync = false;
            }

            public bool IsInFastBlocks { get; set; }
            public bool IsInFastSync { get; set; }
            public bool IsInStateSync { get; set; }
            public bool IsInBeamSync { get; set; }
            public bool IsInFullSync { get; set; }

            /// <summary>
            /// Best block that has been processed
            /// </summary>
            public long Processed { get; }

            /// <summary>
            /// Best full block state in the state trie (may not be processed if we just finished state trie download)
            /// </summary>
            public long State { get; }

            /// <summary>
            /// Best beam block state in the state trie (may not be processed if we just finished state trie download)
            /// </summary>
            public long BeamState { get; }

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

            public UInt256 PeerDifficulty { get; }
        }
    }
}