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
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Timers;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.ParallelSync
{
    public class MultiSyncModeSelector : ISyncModeSelector, IDisposable
    {
        /// <summary>
        /// Number of blocks before the best peer's head when we switch from fast sync to full sync
        /// </summary>
        public const int FastSyncLag = 2;

        /// <summary>
        /// How many blocks can fast sync stay behind while state nodes is still syncing
        /// </summary>
        public const int StickyStateNodesDelta = 32;

        private readonly ISyncProgressResolver _syncProgressResolver;
        private readonly ISyncPeerPool _syncPeerPool;
        private readonly ISyncConfig _syncConfig;
        protected readonly ILogger _logger;

        private long PivotNumber;
        private bool BeamSyncEnabled => _syncConfig.BeamSync;
        private bool FastSyncEnabled => _syncConfig.FastSync;
        private bool FastBlocksEnabled => _syncConfig.FastSync && _syncConfig.FastBlocks;
        private bool FastBodiesEnabled => FastBlocksEnabled && _syncConfig.DownloadBodiesInFastSync;
        private bool FastReceiptsEnabled => FastBlocksEnabled && _syncConfig.DownloadReceiptsInFastSync;
        private bool FastBlocksHeadersFinished => !FastBlocksEnabled || _syncProgressResolver.IsFastBlocksHeadersFinished();
        private bool FastBlocksBodiesFinished => !FastBodiesEnabled || _syncProgressResolver.IsFastBlocksBodiesFinished();
        private bool FastBlocksReceiptsFinished => !FastReceiptsEnabled || _syncProgressResolver.IsFastBlocksReceiptsFinished();
        private long FastSyncCatchUpHeightDelta => _syncConfig.FastSyncCatchUpHeightDelta ?? FastSyncLag;

        internal long? LastBlockThatEnabledFullSync { get; set; }

        private Timer _timer;

        public MultiSyncModeSelector(
            ISyncProgressResolver syncProgressResolver,
            ISyncPeerPool syncPeerPool,
            ISyncConfig syncConfig,
            ILogManager logManager)
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

            _timer = StartUpdateTimer();
        }

        private Timer StartUpdateTimer()
        {
            Timer timer = new();
            timer.Interval = 1000;
            timer.AutoReset = false;
            timer.Elapsed += TimerOnElapsed;
            timer.Enabled = true;
            return timer;
        }

        public void DisableTimer()
        {
            // for testing
            _timer.Stop();
        }

        public void Update()
        {
            SyncMode newModes;
            string reason = string.Empty;
            if (_syncProgressResolver.IsLoadingBlocksFromDb())
            {
                newModes = SyncMode.DbLoad;
            }
            else if (!_syncConfig.SynchronizationEnabled)
            {
                newModes = SyncMode.Disconnected;
                reason = "Synchronization Disabled";
            }
            else
            {
                (UInt256? peerDifficulty, long? peerBlock) = ReloadDataFromPeers();
                // if there are no peers that we could use then we cannot sync
                if (peerDifficulty == null || peerBlock == null || peerBlock == 0)
                {
                    newModes = SyncMode.Disconnected;
                    reason = "No Useful Peers";
                }
                // to avoid expensive checks we make this simple check at the beginning
                else
                {
                    Snapshot best = TakeSnapshot(peerDifficulty.Value, peerBlock.Value);
                    if (!FastSyncEnabled)
                    {

                        bool anyPeers = peerBlock.Value > 0 &&
                                        peerDifficulty.Value >= _syncProgressResolver.ChainDifficulty;
                        newModes = anyPeers ? SyncMode.Full : SyncMode.Disconnected;
                        reason = "No Useful Peers";
                    }
                    else
                    {
                        try
                        {
                            best.IsInFastSync = ShouldBeInFastSyncMode(best);
                            best.IsInStateSync = ShouldBeInStateNodesMode(best);
                            best.IsInBeamSync = ShouldBeInBeamSyncMode(best);
                            best.IsInFullSync = ShouldBeInFullSyncMode(best);
                            best.IsInFastHeaders = ShouldBeInFastHeadersMode(best);
                            best.IsInFastBodies = ShouldBeInFastBodiesMode(best);
                            best.IsInFastReceipts = ShouldBeInFastReceiptsMode(best);
                            best.IsInDisconnected = ShouldBeInDisconnectedMode(best);
                            best.IsInWaitingForBlock = ShouldBeInWaitingForBlockMode(best);
                            newModes = SyncMode.None;
                            CheckAddFlag(best.IsInBeamSync, SyncMode.Beam, ref newModes);
                            CheckAddFlag(best.IsInFastHeaders, SyncMode.FastHeaders, ref newModes);
                            CheckAddFlag(best.IsInFastBodies, SyncMode.FastBodies, ref newModes);
                            CheckAddFlag(best.IsInFastReceipts, SyncMode.FastReceipts, ref newModes);
                            CheckAddFlag(best.IsInFastSync, SyncMode.FastSync, ref newModes);
                            CheckAddFlag(best.IsInFullSync, SyncMode.Full, ref newModes);
                            CheckAddFlag(best.IsInStateSync, SyncMode.StateNodes, ref newModes);
                            CheckAddFlag(best.IsInDisconnected, SyncMode.Disconnected, ref newModes);
                            CheckAddFlag(best.IsInWaitingForBlock, SyncMode.WaitingForBlock, ref newModes);
                            if (IsTheModeSwitchWorthMentioning(newModes))
                            {
                                string stateString = BuildStateString(best);
                                if (_logger.IsInfo)
                                    _logger.Info($"Changing state {Current} to {newModes} at {stateString}");
                            }

                        }
                        catch (InvalidAsynchronousStateException)
                        {
                            newModes = SyncMode.Disconnected;
                            reason = "Snapshot Misalignment";
                        }
                    }

                    if ((newModes & (SyncMode.Full | SyncMode.WaitingForBlock)) != SyncMode.None 
                         && (Current & (SyncMode.Full | SyncMode.WaitingForBlock)) == SyncMode.None)
                    {
                        if(_logger.IsTrace) _logger.Trace($"Setting last full sync switch block to {best.Block}");
                        LastBlockThatEnabledFullSync = best.Block;
                    }
                }
            }


            UpdateSyncModes(newModes, reason);
        }

        private void CheckAddFlag(in bool flag, SyncMode mode, ref SyncMode resultMode)
        {
            if (flag)
            {
                resultMode |= mode;
            }
        }

        private bool IsTheModeSwitchWorthMentioning(SyncMode newModes)
        {
            return _logger.IsDebug ||
                   newModes != Current &&
                   (newModes != SyncMode.WaitingForBlock || Current != SyncMode.Full) &&
                   (newModes != SyncMode.Full || Current != SyncMode.WaitingForBlock);
        }

        private void UpdateSyncModes(SyncMode newModes, string? reason = null)
        {
            if (_logger.IsTrace)
            {
                string message = $"Changing state to {newModes} | {reason}";
                if (_logger.IsTrace) _logger.Trace(message);
            }

            SyncMode previous = Current;

            SyncModeChangedEventArgs args = new(previous, newModes);

            // Changing is invoked here so we can block until all the subsystems are ready to switch
            // for example when switching to Full sync we need to ensure that we safely transition
            // the beam sync DB and beam processor

            Preparing?.Invoke(this, args);
            Changing?.Invoke(this, args);
            Current = newModes;
            Changed?.Invoke(this, args);
        }

        /// <summary>
        /// We display the state in the most likely ascending order
        /// </summary>
        /// <param name="best">Snapshot of the best known states</param>
        /// <returns>A string describing the state of sync</returns>
        private static string BuildStateString(Snapshot best) =>
            $"processed:{best.Processed}|state:{best.State}|block:{best.Block}|header:{best.Header}|peer block:{best.PeerBlock}";

        private void TimerOnElapsed(object? sender, ElapsedEventArgs e)
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

        public SyncMode Current { get; private set; } = SyncMode.Disconnected;

        private bool IsInAStickyFullSyncMode(Snapshot best)
        {
            long bestBlock = Math.Max(best.Processed, LastBlockThatEnabledFullSync ?? 0);
            bool hasEverBeenInFullSync = bestBlock > PivotNumber && best.State > PivotNumber;
            long heightDelta = best.PeerBlock - bestBlock;
            return hasEverBeenInFullSync && heightDelta < FastSyncCatchUpHeightDelta;
        }

        private bool ShouldBeInWaitingForBlockMode(Snapshot best)
        {
            bool noDesiredPeerKnown = !AnyDesiredPeerKnown(best);
            bool postPivotPeerAvailable = AnyPostPivotPeerKnown(best.PeerBlock);
            bool hasFastSyncBeenActive = best.Header >= PivotNumber;
            bool notInBeamSync = !best.IsInBeamSync;
            bool notInFastSync = !best.IsInFastSync;
            bool notInStateSync = !best.IsInStateSync;

            bool result = noDesiredPeerKnown &&
                          postPivotPeerAvailable &&
                          hasFastSyncBeenActive &&
                          notInBeamSync &&
                          notInFastSync &&
                          notInStateSync;

            if (_logger.IsTrace)
            {
                LogDetailedSyncModeChecks("WAITING FOR BLOCK",
                        (nameof(noDesiredPeerKnown), noDesiredPeerKnown),
                        (nameof(postPivotPeerAvailable),postPivotPeerAvailable),
                        (nameof(hasFastSyncBeenActive),hasFastSyncBeenActive),
                        (nameof(notInBeamSync), notInBeamSync),
                        (nameof(notInFastSync), notInFastSync),
                        (nameof(notInStateSync), notInStateSync));
            }

            return result;
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

            bool result =
                postPivotPeerAvailable &&
                // (catch up after node is off for a while
                // OR standard fast sync)
                notInAStickyFullSync &&
                heightDeltaGreaterThanLag &&
                notHasJustStartedFullSync;

            if (_logger.IsTrace)
            {
                LogDetailedSyncModeChecks("FAST",
                    (nameof(postPivotPeerAvailable), postPivotPeerAvailable),
                    (nameof(heightDeltaGreaterThanLag), heightDeltaGreaterThanLag),
                    (nameof(notInAStickyFullSync), notInAStickyFullSync),
                    (nameof(notHasJustStartedFullSync), notHasJustStartedFullSync));
            }

            return result;
        }

        private bool ShouldBeInFullSyncMode(Snapshot best)
        {
            bool desiredPeerKnown = AnyDesiredPeerKnown(best);
            bool postPivotPeerAvailable = AnyPostPivotPeerKnown(best.PeerBlock);
            bool hasFastSyncBeenActive = best.Header >= PivotNumber;
            bool notInBeamSync = !best.IsInBeamSync;
            bool notInFastSync = !best.IsInFastSync;
            bool notInStateSync = !best.IsInStateSync;

            bool result = desiredPeerKnown &&
                          postPivotPeerAvailable &&
                          hasFastSyncBeenActive &&
                          notInBeamSync &&
                          notInFastSync &&
                          notInStateSync;

            if (_logger.IsTrace)
            {
                LogDetailedSyncModeChecks("FULL",
                    (nameof(desiredPeerKnown), desiredPeerKnown),
                    (nameof(postPivotPeerAvailable), postPivotPeerAvailable),
                    (nameof(hasFastSyncBeenActive), hasFastSyncBeenActive),
                    (nameof(notInBeamSync), notInBeamSync),
                    (nameof(notInFastSync), notInFastSync),
                    (nameof(notInStateSync), notInStateSync));
            }

            return result;
        }

        // ReSharper disable once UnusedParameter.Local
        private bool ShouldBeInFastHeadersMode(Snapshot best)
        {
            bool fastBlocksHeadersNotFinished = !FastBlocksHeadersFinished;

            if (_logger.IsTrace)
            {
                LogDetailedSyncModeChecks("HEADERS",
                    (nameof(fastBlocksHeadersNotFinished), fastBlocksHeadersNotFinished));
            }

            // this is really the only condition - fast blocks headers can always run if there are peers until it is done
            // also fast blocks headers can run in parallel with all other sync modes
            return fastBlocksHeadersNotFinished;
        }

        private bool ShouldBeInFastBodiesMode(Snapshot best)
        {
            bool fastBodiesNotFinished = !FastBlocksBodiesFinished;
            bool fastHeadersFinished = FastBlocksHeadersFinished;
            bool notInStateSync = !best.IsInStateSync;
            bool stateSyncFinished = best.State > 0;

            // fast blocks bodies can run if there are peers until it is done
            // fast blocks bodies can run in parallel with full sync when headers are finished
            bool result = fastBodiesNotFinished && fastHeadersFinished && notInStateSync && stateSyncFinished;

            if (_logger.IsTrace)
            {
                LogDetailedSyncModeChecks("BODIES",
                    (nameof(fastBodiesNotFinished), fastBodiesNotFinished),
                    (nameof(fastHeadersFinished), fastHeadersFinished),
                    (nameof(notInStateSync), notInStateSync),
                    (nameof(stateSyncFinished), stateSyncFinished));
            }

            return result;
        }

        private bool ShouldBeInFastReceiptsMode(Snapshot best)
        {
            bool fastReceiptsNotFinished = !FastBlocksReceiptsFinished;
            bool fastBodiesFinished = FastBlocksBodiesFinished;
            bool notInStateSync = !best.IsInStateSync;
            bool stateSyncFinished = best.State > 0;

            // fast blocks receipts can run if there are peers until it is done
            // fast blocks receipts can run in parallel with full sync when bodies are finished
            bool result = fastReceiptsNotFinished && fastBodiesFinished && notInStateSync && stateSyncFinished;
            
            if (_logger.IsTrace)
            {
                LogDetailedSyncModeChecks("RECEIPTS",
                    (nameof(fastReceiptsNotFinished), fastReceiptsNotFinished),
                    (nameof(fastBodiesFinished), fastBodiesFinished),
                    (nameof(notInStateSync), notInStateSync),
                    (nameof(stateSyncFinished), stateSyncFinished));
            }

            // fast blocks receipts can run if there are peers until it is done
            // fast blocks receipts can run in parallel with full sync when bodies are finished
            return result;
        }

        private bool ShouldBeInDisconnectedMode(Snapshot best)
        {
            return !best.IsInBeamSync &&
                   !best.IsInFastBodies &&
                   !best.IsInFastHeaders &&
                   !best.IsInFastReceipts &&
                   !best.IsInFastSync &&
                   !best.IsInFullSync &&
                   !best.IsInStateSync &&
                   // maybe some more sophisticated heuristic?
                   best.PeerDifficulty == UInt256.Zero;
        }

        private bool ShouldBeInStateNodesMode(Snapshot best)
        {
            bool fastSyncEnabled = FastSyncEnabled;
            bool hasFastSyncBeenActive = best.Header >= PivotNumber;
            bool hasAnyPostPivotPeer = AnyPostPivotPeerKnown(best.PeerBlock);
            bool notInFastSync = !best.IsInFastSync;
            bool stickyStateNodes = best.PeerBlock - best.Header < (FastSyncLag + StickyStateNodesDelta);
            bool stateNotDownloadedYet = (best.PeerBlock - best.State > FastSyncLag ||
                                          best.Header > best.State && best.Header > best.Block);
            bool notInAStickyFullSync = !IsInAStickyFullSyncMode(best);
            bool notHasJustStartedFullSync = !HasJustStartedFullSync(best);

            bool result = fastSyncEnabled &&
                          hasFastSyncBeenActive &&
                          hasAnyPostPivotPeer &&
                          (notInFastSync || stickyStateNodes) &&
                          stateNotDownloadedYet &&
                          notHasJustStartedFullSync &&
                          notInAStickyFullSync;
            
            if (_logger.IsTrace)
            {
                LogDetailedSyncModeChecks("STATE",
                    (nameof(fastSyncEnabled), fastSyncEnabled),
                    (nameof(hasFastSyncBeenActive), hasFastSyncBeenActive),
                    (nameof(hasAnyPostPivotPeer), hasAnyPostPivotPeer),
                    (nameof(notInFastSync), notInFastSync),
                    (nameof(stateNotDownloadedYet), stateNotDownloadedYet),
                    (nameof(notInAStickyFullSync), notInAStickyFullSync),
                    (nameof(notHasJustStartedFullSync), notHasJustStartedFullSync));
            }

            return result;
        }

        private bool ShouldBeInBeamSyncMode(Snapshot best)
        {
            bool beamSyncEnabled = BeamSyncEnabled;
            bool isInStateSync = best.IsInStateSync;

            bool result = beamSyncEnabled &&
                          isInStateSync;

            if (_logger.IsTrace)
            {
                LogDetailedSyncModeChecks("BEAM",
                    (nameof(beamSyncEnabled), beamSyncEnabled),
                    (nameof(isInStateSync), isInStateSync));
            }

            return result;
        }

        private bool HasJustStartedFullSync(Snapshot best) =>
            best.State > PivotNumber // we have saved some root
            && (best.State == best.Header || best.Header == best.Block) // and we do not need to catch up to headers anymore 
            && best.Processed < best.State; // not processed the block yet

        protected virtual bool AnyDesiredPeerKnown(Snapshot best)
        {
            UInt256 localChainDifficulty = _syncProgressResolver.ChainDifficulty;
            bool anyDesiredPeerKnown = best.PeerDifficulty > localChainDifficulty
                                       || best.PeerDifficulty == localChainDifficulty && best.PeerBlock > best.Header;
            if (anyDesiredPeerKnown)
            {
                if (_logger.IsTrace)
                    _logger.Trace($"   Best peer [{best.PeerBlock},{best.PeerDifficulty}] " +
                                  $"> local [{best.Header},{localChainDifficulty}]");
            }

            return anyDesiredPeerKnown;
        }

        private bool AnyPostPivotPeerKnown(long bestPeerBlock) => bestPeerBlock > _syncConfig.PivotNumberParsed;

        private (UInt256? maxPeerDifficulty, long? number) ReloadDataFromPeers()
        {
            UInt256? maxPeerDifficulty = null;
            long? number = 0;

            foreach (PeerInfo peer in _syncPeerPool.InitializedPeers)
            {
                UInt256 currentMax = maxPeerDifficulty ?? UInt256.Zero;
                if (peer.TotalDifficulty > currentMax)
                {
                    // we don't trust parity TotalDifficulty, so we are checking if we know the hash and get our total difficulty
                    var realTotalDifficulty = _syncProgressResolver.GetTotalDifficulty(peer.HeadHash) ?? peer.TotalDifficulty;
                    if (realTotalDifficulty > currentMax)
                    {
                        maxPeerDifficulty = realTotalDifficulty;
                        number = peer.HeadNumber;
                    }
                }
            }

            return (maxPeerDifficulty, number);
        }

        public void Dispose()
        {
            _timer.Dispose();
        }

        private Snapshot TakeSnapshot(UInt256 peerDifficulty, long peerBlock)
        {
            // need to find them in the reversed order otherwise we may fall behind the processing
            // and think that we have an invalid snapshot
            long processed = _syncProgressResolver.FindBestProcessedBlock();
            long state = _syncProgressResolver.FindBestFullState();
            long block = _syncProgressResolver.FindBestFullBlock();
            long header = _syncProgressResolver.FindBestHeader();

            Snapshot best = new(processed, state, block, header, peerBlock, peerDifficulty);
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

        private string GetBoolFlagString(in bool flag) => flag ? string.Empty : "!";

        private void LogDetailedSyncModeChecks(string syncType, params (string Name, bool IsSatisfied)[] checks)
        {
            List<string> matched = new();
            List<string> failed = new();

            foreach ((string Name, bool IsSatisfied) check in checks)
            {
                if (check.IsSatisfied)
                {
                    matched.Add(check.Name);
                }
                else
                {
                    failed.Add(check.Name);
                }
            }

            bool result = checks.All(c => c.IsSatisfied);
            _logger.Trace(
                $"{(result ? " * " : "   ")}{syncType.PadRight(20)}: yes({string.Join(", ", matched)}), no({string.Join(", ", failed)})");
        }

        public event EventHandler<SyncModeChangedEventArgs>? Preparing;
        public event EventHandler<SyncModeChangedEventArgs>? Changing;
        public event EventHandler<SyncModeChangedEventArgs>? Changed;

        protected ref struct Snapshot
        {
            public Snapshot(long processed, long state, long block, long header, long peerBlock, UInt256 peerDifficulty)
            {
                Processed = processed;
                State = state;
                Block = block;
                Header = header;
                PeerBlock = peerBlock;
                PeerDifficulty = peerDifficulty;

                IsInWaitingForBlock = IsInDisconnected = IsInFastReceipts = IsInFastBodies = IsInFastHeaders = IsInFastSync = IsInBeamSync = IsInFullSync = IsInStateSync = false;
            }

            public bool IsInFastHeaders { get; set; }
            public bool IsInFastBodies { get; set; }
            public bool IsInFastReceipts { get; set; }
            public bool IsInFastSync { get; set; }
            public bool IsInStateSync { get; set; }
            public bool IsInBeamSync { get; set; }
            public bool IsInFullSync { get; set; }
            public bool IsInDisconnected { get; set; }
            public bool IsInWaitingForBlock { get; set; }

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

            public UInt256 PeerDifficulty { get; }
        }
    }
}
