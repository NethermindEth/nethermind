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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Snap;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.ParallelSync
{
    /// <summary>
    /// Chooses <see cref="Current"/> sync mode.
    /// </summary>
    /// <remarks>
    /// Does an update of <see cref="Current"/> sync mode every 1s. Announcing <see cref="Changed"/> that anyone can  subscribe too.
    /// Mostly <see cref="SyncFeed{T}"/> are listening to that update.
    ///
    /// New Beacon rules:
    /// * <see cref="SyncMode.BeaconHeaders"/> and Beacon Mode (<see cref="SyncMode.WaitingForBlock"/> from beacon node) are exclusive:
    ///     - Beacon modes have higher priority than conflicting modes.
    ///     - Their are enabled based on <see cref="IBeaconSyncStrategy.ShouldBeInBeaconHeaders"/> and <see cref="IBeaconSyncStrategy.ShouldBeInBeaconModeControl"/>.
    /// * When <see cref="ISyncConfig.FastSync"/> is enabled:
    ///     - Beacon modes are exclusive with <see cref="SyncMode.FastSync"/>, <see cref="SyncMode.Full"/>, <see cref="SyncMode.StateNodes"/> and <see cref="SyncMode.SnapSync"/>.
    ///     - Beacon modes can run parallel with syncing old state (<see cref="SyncMode.FastHeaders"/>, <see cref="SyncMode.FastBlocks"/> and <see cref="SyncMode.FastReceipts"/>).
    /// * When <see cref="ISyncConfig.FastSync"/> is disabled:
    ///     - Beacon modes are allied directly.
    ///     - If no Beacon mode is applied and we have good peers on the network we apply <see cref="SyncMode.Full"/>,.
    /// </remarks>
    public class MultiSyncModeSelector : ISyncModeSelector, IDisposable
    {
        /// <summary>
        /// Number of blocks before the best peer's head when we switch from fast sync to full sync
        /// </summary>
        public const int FastSyncLag = 32;

        /// <summary>
        /// How many blocks can fast sync stay behind while state nodes is still syncing
        /// </summary>
        private const int StickyStateNodesDelta = 32;

        private readonly ISyncProgressResolver _syncProgressResolver;
        private readonly ISyncPeerPool _syncPeerPool;
        private readonly ISyncConfig _syncConfig;
        private readonly IBeaconSyncStrategy _beaconSyncStrategy;
        private readonly IBetterPeerStrategy _betterPeerStrategy;
        private readonly bool _needToWaitForHeaders;
        private readonly ILogger _logger;
        private readonly bool _isSnapSyncDisabledAfterAnyStateSync;

        private readonly long _pivotNumber;
        private bool FastSyncEnabled => _syncConfig.FastSync;
        private bool SnapSyncEnabled => _syncConfig.SnapSync && !_isSnapSyncDisabledAfterAnyStateSync;
        private bool FastBlocksEnabled => _syncConfig.FastSync && _syncConfig.FastBlocks;
        private bool FastBodiesEnabled => FastBlocksEnabled && _syncConfig.DownloadBodiesInFastSync;
        private bool FastReceiptsEnabled => FastBlocksEnabled && _syncConfig.DownloadReceiptsInFastSync;
        private bool FastBlocksHeadersFinished => !FastBlocksEnabled || _syncProgressResolver.IsFastBlocksHeadersFinished();
        private bool FastBlocksBodiesFinished => !FastBodiesEnabled || _syncProgressResolver.IsFastBlocksBodiesFinished();
        private bool FastBlocksReceiptsFinished => !FastReceiptsEnabled || _syncProgressResolver.IsFastBlocksReceiptsFinished();
        private long FastSyncCatchUpHeightDelta => _syncConfig.FastSyncCatchUpHeightDelta ?? FastSyncLag;
        private bool NotNeedToWaitForHeaders => !_needToWaitForHeaders || FastBlocksHeadersFinished;
        private long? LastBlockThatEnabledFullSync { get; set; }

        private readonly CancellationTokenSource _cancellation = new();

        public event EventHandler<SyncModeChangedEventArgs>? Preparing;
        public event EventHandler<SyncModeChangedEventArgs>? Changing;
        public event EventHandler<SyncModeChangedEventArgs>? Changed;

        public SyncMode Current { get; private set; } = SyncMode.Disconnected;

        public MultiSyncModeSelector(
            ISyncProgressResolver syncProgressResolver,
            ISyncPeerPool syncPeerPool,
            ISyncConfig syncConfig,
            IBeaconSyncStrategy beaconSyncStrategy,
            IBetterPeerStrategy betterPeerStrategy,
            ILogManager logManager,
            bool needToWaitForHeaders = false)
        {
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _beaconSyncStrategy = beaconSyncStrategy ?? throw new ArgumentNullException(nameof(beaconSyncStrategy));
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _betterPeerStrategy = betterPeerStrategy ?? throw new ArgumentNullException(nameof(betterPeerStrategy));
            _syncProgressResolver = syncProgressResolver ?? throw new ArgumentNullException(nameof(syncProgressResolver));
            _needToWaitForHeaders = needToWaitForHeaders;

            if (syncConfig.FastSyncCatchUpHeightDelta <= FastSyncLag)
            {
                if (_logger.IsWarn)
                    _logger.Warn($"'FastSyncCatchUpHeightDelta' parameter is less or equal to {FastSyncLag}, which is a threshold of blocks always downloaded in full sync. 'FastSyncCatchUpHeightDelta' will have no effect.");
            }

            _pivotNumber = _syncConfig.PivotNumberParsed;
            _isSnapSyncDisabledAfterAnyStateSync = _syncProgressResolver.FindBestFullState() != 0;

            _ = StartAsync(_cancellation.Token);
        }

        private async Task StartAsync(CancellationToken cancellationToken)
        {
            PeriodicTimer timer = new (TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    try
                    {
                        Update();
                    }
                    catch (Exception exception)
                    {
                        if (_logger.IsError) _logger.Error("Sync mode update failed", exception);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (_logger.IsInfo) _logger.Info("Sync mode selector stopped");
            }
        }

        public void Stop()
        {
            _cancellation.Cancel();
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
                bool inBeaconControl = _beaconSyncStrategy.ShouldBeInBeaconModeControl();
                (UInt256? peerDifficulty, long? peerBlock) = ReloadDataFromPeers();
                // if there are no peers that we could use then we cannot sync
                if (peerDifficulty == null || peerBlock == null || peerBlock == 0)
                {
                    newModes = inBeaconControl ? SyncMode.WaitingForBlock : SyncMode.Disconnected;
                    reason = "No Useful Peers";
                }
                // to avoid expensive checks we make this simple check at the beginning
                else
                {
                    Snapshot best = TakeSnapshot(peerDifficulty.Value, peerBlock.Value, inBeaconControl);
                    best.IsInBeaconHeaders = _beaconSyncStrategy.ShouldBeInBeaconHeaders();

                    if (!FastSyncEnabled)
                    {
                        best.IsInWaitingForBlock = ShouldBeInWaitingForBlockMode(best);

                        if (best.IsInWaitingForBlock)
                        {
                            newModes = SyncMode.WaitingForBlock;
                        }
                        else if (best.IsInBeaconHeaders)
                        {
                            newModes = SyncMode.BeaconHeaders;
                        }
                        else
                        {
                            if (ShouldBeInFullSyncModeInArchiveMode(best))
                            {
                                newModes = SyncMode.Full;
                            }
                            else
                            {
                                newModes = SyncMode.Disconnected;
                                reason = "No Useful Peers";
                            }
                        }
                    }
                    else
                    {
                        try
                        {
                            best.IsInFastSync = ShouldBeInFastSyncMode(best);
                            best.IsInStateSync = ShouldBeInStateSyncMode(best);
                            best.IsInFullSync = ShouldBeInFullSyncMode(best);
                            best.IsInFastHeaders = ShouldBeInFastHeadersMode(best);
                            best.IsInFastBodies = ShouldBeInFastBodiesMode(best);
                            best.IsInFastReceipts = ShouldBeInFastReceiptsMode(best);
                            best.IsInDisconnected = ShouldBeInDisconnectedMode(best);
                            best.IsInWaitingForBlock = ShouldBeInWaitingForBlockMode(best);
                            bool canBeInSnapRangesPhase = CanBeInSnapRangesPhase(best);

                            newModes = SyncMode.None;
                            CheckAddFlag(best.IsInBeaconHeaders, SyncMode.BeaconHeaders, ref newModes);
                            CheckAddFlag(best.IsInFastHeaders, SyncMode.FastHeaders, ref newModes);
                            CheckAddFlag(best.IsInFastBodies, SyncMode.FastBodies, ref newModes);
                            CheckAddFlag(best.IsInFastReceipts, SyncMode.FastReceipts, ref newModes);
                            CheckAddFlag(best.IsInFastSync, SyncMode.FastSync, ref newModes);
                            CheckAddFlag(best.IsInFullSync, SyncMode.Full, ref newModes);
                            CheckAddFlag(best.IsInStateSync, canBeInSnapRangesPhase ? SyncMode.SnapSync : SyncMode.StateNodes, ref newModes);
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
                        if (_logger.IsTrace) _logger.Trace($"Setting last full sync switch block to {best.Block}");
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
            // DBS and processors if needed

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
            $"processed:{best.Processed}|state:{best.State}|block:{best.Block}|header:{best.Header}|chain difficulty:{best.ChainDifficulty}|peer block:{best.Peer.Block}|peer total difficulty:{best.Peer.TotalDifficulty}";

        private bool IsInAStickyFullSyncMode(Snapshot best)
        {
            long bestBlock = Math.Max(best.Processed, LastBlockThatEnabledFullSync ?? 0);
            bool hasEverBeenInFullSync = bestBlock > _pivotNumber && best.State > _pivotNumber;
            long heightDelta = best.Peer.Block - bestBlock;
            return hasEverBeenInFullSync && heightDelta < FastSyncCatchUpHeightDelta;
        }

        private bool ShouldBeInWaitingForBlockMode(Snapshot best)
        {
            bool inBeaconControl = best.IsInBeaconControl;
            bool notInBeaconHeaders = !best.IsInBeaconHeaders;
            bool noDesiredPeerKnown = !AnyDesiredPeerKnown(best);
            bool postPivotPeerAvailable = AnyPostPivotPeerKnown(best.Peer.Block);
            bool hasFastSyncBeenActive = best.Header >= _pivotNumber;
            bool notInFastSync = !best.IsInFastSync;
            bool notInStateSync = !best.IsInStateSync;

            bool result = inBeaconControl ||
                          (notInBeaconHeaders &&
                           noDesiredPeerKnown &&
                           postPivotPeerAvailable &&
                           hasFastSyncBeenActive &&
                           notInFastSync &&
                           notInStateSync);

            if (_logger.IsTrace)
            {
                LogDetailedSyncModeChecks("WAITING FOR BLOCK",
                    (nameof(inBeaconControl), inBeaconControl),
                    (nameof(notInBeaconHeaders), notInBeaconHeaders),
                    (nameof(noDesiredPeerKnown), noDesiredPeerKnown),
                    (nameof(postPivotPeerAvailable), postPivotPeerAvailable),
                    (nameof(hasFastSyncBeenActive), hasFastSyncBeenActive),
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

            if (_syncConfig.FastBlocks && _pivotNumber != 0 && best.Header == 0)
            {
                // do not start fast sync until at least one header is downloaded or we would start from zero
                // we are fine to start from zero if we do not use fast blocks
                return false;
            }

            long bestPeerBlock = best.Peer.Block;

            bool notInBeaconModes = !best.IsInAnyBeaconMode;
            long heightDelta = bestPeerBlock - best.Header;
            bool heightDeltaGreaterThanLag = heightDelta > FastSyncLag;
            bool postPivotPeerAvailable = AnyPostPivotPeerKnown(bestPeerBlock);
            bool notInAStickyFullSync = !IsInAStickyFullSyncMode(best);
            bool notHasJustStartedFullSync = !HasJustStartedFullSync(best);
            bool notNeedToWaitForHeaders = NotNeedToWaitForHeaders;

            bool result = notInBeaconModes &&
                          postPivotPeerAvailable &&
                          // (catch up after node is off for a while
                          // OR standard fast sync)
                          notInAStickyFullSync &&
                          heightDeltaGreaterThanLag &&
                          notHasJustStartedFullSync &&
                          notNeedToWaitForHeaders;

            if (_logger.IsTrace)
            {
                LogDetailedSyncModeChecks("FAST",
                    (nameof(notInBeaconModes), notInBeaconModes),
                    (nameof(postPivotPeerAvailable), postPivotPeerAvailable),
                    (nameof(heightDeltaGreaterThanLag), heightDeltaGreaterThanLag),
                    (nameof(notInAStickyFullSync), notInAStickyFullSync),
                    (nameof(notHasJustStartedFullSync), notHasJustStartedFullSync),
                    (nameof(notNeedToWaitForHeaders), notNeedToWaitForHeaders));
            }

            return result;
        }

        private bool ShouldBeInFullSyncMode(Snapshot best)
        {
            bool notInBeaconModes = !best.IsInAnyBeaconMode;
            bool desiredPeerKnown = AnyDesiredPeerKnown(best) ;
            bool postPivotPeerAvailable = AnyPostPivotPeerKnown(best.Peer.Block);
            bool hasFastSyncBeenActive = best.Header >= _pivotNumber;
            bool notInFastSync = !best.IsInFastSync;
            bool notInStateSync = !best.IsInStateSync;
            bool notNeedToWaitForHeaders = NotNeedToWaitForHeaders;

            bool result = notInBeaconModes &&
                          desiredPeerKnown &&
                          postPivotPeerAvailable &&
                          hasFastSyncBeenActive &&
                          notInFastSync &&
                          notInStateSync &&
                          notNeedToWaitForHeaders;

            if (_logger.IsTrace)
            {
                LogDetailedSyncModeChecks("FULL",
                    (nameof(notInBeaconModes), notInBeaconModes),
                    (nameof(desiredPeerKnown), desiredPeerKnown),
                    (nameof(postPivotPeerAvailable), postPivotPeerAvailable),
                    (nameof(hasFastSyncBeenActive), hasFastSyncBeenActive),
                    (nameof(notInFastSync), notInFastSync),
                    (nameof(notInStateSync), notInStateSync),
                    (nameof(notNeedToWaitForHeaders), notNeedToWaitForHeaders));
            }

            return result;
        }

        private bool ShouldBeInFullSyncModeInArchiveMode(Snapshot best)
        {
            bool notInBeaconModes = !best.IsInAnyBeaconMode;
            bool desiredPeerKnown = AnyDesiredPeerKnown(best);

            bool result = notInBeaconModes &&
                          desiredPeerKnown;

            if (_logger.IsTrace)
            {
                LogDetailedSyncModeChecks("FULL",
                    (nameof(notInBeaconModes), notInBeaconModes),
                    (nameof(desiredPeerKnown), desiredPeerKnown));
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
            return !best.IsInFastBodies &&
                   !best.IsInFastHeaders &&
                   !best.IsInFastReceipts &&
                   !best.IsInFastSync &&
                   !best.IsInFullSync &&
                   !best.IsInStateSync &&
                   // maybe some more sophisticated heuristic?
                   best.Peer.TotalDifficulty == UInt256.Zero;
        }

        private bool ShouldBeInStateSyncMode(Snapshot best)
        {
            long bestPeerBlock = best.Peer.Block;

            bool fastSyncEnabled = FastSyncEnabled;
            bool notInBeaconModes = !best.IsInAnyBeaconMode;
            bool hasFastSyncBeenActive = best.Header >= _pivotNumber;
            bool hasAnyPostPivotPeer = AnyPostPivotPeerKnown(bestPeerBlock);
            bool notInFastSync = !best.IsInFastSync;
            bool notNeedToWaitForHeaders = NotNeedToWaitForHeaders;
            bool stickyStateNodes = bestPeerBlock - best.Header < (FastSyncLag + StickyStateNodesDelta);
            bool stateNotDownloadedYet = (bestPeerBlock - best.State > FastSyncLag ||
                                          best.Header > best.State && best.Header > best.Block);
            bool notInAStickyFullSync = !IsInAStickyFullSyncMode(best);
            bool notHasJustStartedFullSync = !HasJustStartedFullSync(best);


            bool result = fastSyncEnabled &&
                          notInBeaconModes &&
                          hasFastSyncBeenActive &&
                          hasAnyPostPivotPeer &&
                          (notInFastSync || stickyStateNodes) &&
                          stateNotDownloadedYet &&
                          notHasJustStartedFullSync &&
                          notInAStickyFullSync &&
                          notNeedToWaitForHeaders;

            if (_logger.IsTrace)
            {
                LogDetailedSyncModeChecks("STATE",
                    (nameof(fastSyncEnabled), fastSyncEnabled),
                    (nameof(hasFastSyncBeenActive), hasFastSyncBeenActive),
                    (nameof(hasAnyPostPivotPeer), hasAnyPostPivotPeer),
                    (nameof(notInFastSync), notInFastSync),
                    (nameof(stateNotDownloadedYet), stateNotDownloadedYet),
                    (nameof(notInAStickyFullSync), notInAStickyFullSync),
                    (nameof(notHasJustStartedFullSync), notHasJustStartedFullSync),
                    (nameof(notNeedToWaitForHeaders), notNeedToWaitForHeaders));
            }

            return result;
        }

        private bool CanBeInSnapRangesPhase(Snapshot best)
        {
            long peerBlock = best.Peer.Block;
            bool isCloseToHead = peerBlock >= best.Header && (peerBlock - best.Header) < Constants.MaxDistanceFromHead;
            bool snapNotFinished = !_syncProgressResolver.IsSnapGetRangesFinished();

            if (_logger.IsTrace)
            {
                LogDetailedSyncModeChecks("SNAP_RANGES",
                    (nameof(SnapSyncEnabled), SnapSyncEnabled),
                    (nameof(isCloseToHead), isCloseToHead),
                    (nameof(snapNotFinished), snapNotFinished));
            }

            return SnapSyncEnabled
                && isCloseToHead
                && snapNotFinished;
        }

        private bool HasJustStartedFullSync(Snapshot best) =>
            best.State > _pivotNumber // we have saved some root
            && (best.State == best.Header || best.Header == best.Block) // and we do not need to catch up to headers anymore
            && best.Processed < best.State; // not processed the block yet

        private bool AnyDesiredPeerKnown(Snapshot best) => _betterPeerStrategy.IsDesiredPeer(best.Peer, (best.ChainDifficulty, best.Header));

        private bool AnyPostPivotPeerKnown(long bestPeerBlock) => bestPeerBlock > _syncConfig.PivotNumberParsed;

        private (UInt256? maxPeerDifficulty, long? number) ReloadDataFromPeers()
        {
            UInt256? maxPeerDifficulty = null;
            long? number = 0;

            foreach (PeerInfo peer in _syncPeerPool.InitializedPeers)
            {
                UInt256 currentMax = maxPeerDifficulty ?? UInt256.Zero;
                long currentMaxNumber = number ?? 0;
                bool isNewPeerBetterThanCurrentMax = _betterPeerStrategy.Compare((currentMax, currentMaxNumber), peer.SyncPeer) < 0;
                if (isNewPeerBetterThanCurrentMax)
                {
                    // we don't trust parity TotalDifficulty, so we are checking if we know the hash and get our total difficulty
                    UInt256 realTotalDifficulty = _syncProgressResolver.GetTotalDifficulty(peer.HeadHash) ?? peer.TotalDifficulty;

                    // during the beacon header sync our realTotalDifficulty could be 0. We're using peer.TotalDifficulty in this case
                    realTotalDifficulty = realTotalDifficulty == 0 ? peer.TotalDifficulty : realTotalDifficulty;
                    bool isRealPeerBetterThanCurrentMax = _betterPeerStrategy.Compare(((currentMax, currentMaxNumber)), (realTotalDifficulty, peer.HeadNumber)) < 0;

                    if (isRealPeerBetterThanCurrentMax)
                    {
                        maxPeerDifficulty = realTotalDifficulty;
                        number = peer.HeadNumber;
                    }
                }
            }

            return (maxPeerDifficulty, number);
        }

        public void Dispose() => _cancellation.Dispose();

        private Snapshot TakeSnapshot(in UInt256 peerDifficulty, long peerBlock, bool inBeaconControl)
        {
            // need to find them in the reversed order otherwise we may fall behind the processing
            // and think that we have an invalid snapshot
            long processed = _syncProgressResolver.FindBestProcessedBlock();
            long state = _syncProgressResolver.FindBestFullState();
            long block = _syncProgressResolver.FindBestFullBlock();
            long header = _syncProgressResolver.FindBestHeader();
            UInt256 chainDifficulty = _syncProgressResolver.ChainDifficulty;

            Snapshot best = new(processed, state, block, header, chainDifficulty, peerBlock, peerDifficulty, inBeaconControl);
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
                || best.Peer.Block < 0
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
            string text = $"{(result ? " * " : "   ")}{syncType.PadRight(20)}: yes({string.Join(", ", matched)}), no({string.Join(", ", failed)})";
            _logger.Trace(text);
        }

        private ref struct Snapshot
        {
            public Snapshot(long processed, long state, long block, long header, UInt256 chainDifficulty, long peerBlock, in UInt256 peerDifficulty, bool isInBeaconControl)
            {
                Processed = processed;
                State = state;
                Block = block;
                Header = header;
                ChainDifficulty = chainDifficulty;
                Peer = (peerDifficulty, peerBlock);
                IsInBeaconControl = isInBeaconControl;

                IsInWaitingForBlock = IsInDisconnected = IsInFastReceipts = IsInFastBodies = IsInFastHeaders
                    = IsInFastSync = IsInFullSync = IsInStateSync = IsInBeaconHeaders = false;
            }

            public bool IsInFastHeaders { get; set; }
            public bool IsInFastBodies { get; set; }
            public bool IsInFastReceipts { get; set; }
            public bool IsInFastSync { get; set; }
            public bool IsInStateSync { get; set; }
            public bool IsInFullSync { get; set; }
            public bool IsInDisconnected { get; set; }
            public bool IsInWaitingForBlock { get; set; }
            public bool IsInBeaconHeaders { get; set; }
            public bool IsInBeaconControl { get; }
            public bool IsInAnyBeaconMode => IsInBeaconHeaders || IsInBeaconControl;

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
            /// Current difficulty of the chain
            /// </summary>
            public UInt256 ChainDifficulty { get; }

            /// <summary>
            /// Best peer block - this is what other peers are advertising - it may be lower than our best block if we get disconnected from best peers
            /// </summary>
            public (UInt256 TotalDifficulty, long Block) Peer { get; }
        }
    }
}
