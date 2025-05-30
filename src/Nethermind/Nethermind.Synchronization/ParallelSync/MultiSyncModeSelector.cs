// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.ServiceStopper;
using Nethermind.Int256;
using Nethermind.Logging;
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
    ///     - Beacon modes can run parallel with syncing old state (<see cref="SyncMode.FastHeaders"/>, <see cref="SyncMode.FastBlocks"/> (the default and always on) and <see cref="SyncMode.FastReceipts"/>).
    /// * When <see cref="ISyncConfig.FastSync"/> is disabled:
    ///     - Beacon modes are allied directly.
    ///     - If no Beacon mode is applied and we have good peers on the network we apply <see cref="SyncMode.Full"/>,.
    /// </remarks>
    public class MultiSyncModeSelector : ISyncModeSelector
    {
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

        private bool FastSyncEnabled => _syncConfig.FastSync;
        private bool SnapSyncEnabled => _syncConfig.SnapSync && !_isSnapSyncDisabledAfterAnyStateSync;
        private bool FastBodiesEnabled => FastSyncEnabled && _syncConfig.DownloadBodiesInFastSync;
        private bool FastReceiptsEnabled => FastSyncEnabled && _syncConfig.DownloadReceiptsInFastSync;
        private bool FastBlocksHeadersFinished => !FastSyncEnabled || _syncProgressResolver.IsFastBlocksHeadersFinished();
        private bool FastBlocksBodiesFinished => !FastBodiesEnabled || _syncProgressResolver.IsFastBlocksBodiesFinished();
        private bool FastBlocksReceiptsFinished => !FastReceiptsEnabled || _syncProgressResolver.IsFastBlocksReceiptsFinished();
        private long FastSyncCatchUpHeightDelta => _syncConfig.FastSyncCatchUpHeightDelta ?? _syncConfig.StateMinDistanceFromHead;
        private bool NotNeedToWaitForHeaders => !_needToWaitForHeaders || FastBlocksHeadersFinished;
        private long? LastBlockThatEnabledFullSync { get; set; }
        private int TotalSyncLag => _syncConfig.StateMinDistanceFromHead + _syncConfig.HeaderStateDistance;

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
            ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _beaconSyncStrategy = beaconSyncStrategy ?? throw new ArgumentNullException(nameof(beaconSyncStrategy));
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _betterPeerStrategy = betterPeerStrategy ?? throw new ArgumentNullException(nameof(betterPeerStrategy));
            _syncProgressResolver = syncProgressResolver ?? throw new ArgumentNullException(nameof(syncProgressResolver));
            _needToWaitForHeaders = syncConfig.NeedToWaitForHeader;

            if (syncConfig.FastSyncCatchUpHeightDelta <= syncConfig.StateMinDistanceFromHead)
            {
                if (_logger.IsWarn)
                    _logger.Warn($"'FastSyncCatchUpHeightDelta' parameter is less or equal to {syncConfig.StateMinDistanceFromHead}, which is a threshold of blocks always downloaded in full sync. 'FastSyncCatchUpHeightDelta' will have no effect.");
            }

            _isSnapSyncDisabledAfterAnyStateSync = _syncProgressResolver.FindBestFullState() != 0;

            _ = StartAsync(_cancellation.Token);
        }

        private async Task StartAsync(CancellationToken cancellationToken)
        {
            PeriodicTimer timer = new(TimeSpan.FromMilliseconds(_syncConfig.MultiSyncModeSelectorLoopTimerMs));
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

        public Task StopAsync()
        {
            return _cancellation.CancelAsync();
        }

        string IStoppableService.Description => "sync mode selector";

        public void Update()
        {
            bool shouldBeInUpdatingPivot = ShouldBeInUpdatingPivot();

            SyncMode newModes;
            string reason = string.Empty;
            if (_syncProgressResolver.IsLoadingBlocksFromDb())
            {
                newModes = SyncMode.DbLoad;
                if (shouldBeInUpdatingPivot)
                {
                    newModes |= SyncMode.UpdatingPivot;
                }
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
                if (peerDifficulty is null || peerBlock is null || peerBlock == 0)
                {
                    newModes = shouldBeInUpdatingPivot ? SyncMode.UpdatingPivot : inBeaconControl ? SyncMode.WaitingForBlock : SyncMode.Disconnected;
                    reason = "No Useful Peers";
                }
                // to avoid expensive checks we make this simple check at the beginning
                else
                {
                    Snapshot best = EnsureSnapshot(peerDifficulty.Value, peerBlock.Value, inBeaconControl);
                    best.IsInBeaconHeaders = ShouldBeInBeaconHeaders(shouldBeInUpdatingPivot);

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
                            best.IsInUpdatingPivot = shouldBeInUpdatingPivot;
                            best.IsInFastSync = ShouldBeInFastSyncMode(best);
                            best.IsInStateSync = ShouldBeInStateSyncMode(best);
                            best.IsInStateNodes = ShouldBeInStateNodesMode(best);
                            best.IsInSnapRanges = ShouldBeInSnapRangesPhase(best);
                            best.IsInFastHeaders = ShouldBeInFastHeadersMode(best);
                            best.IsInFastBodies = ShouldBeInFastBodiesMode(best);
                            best.IsInFastReceipts = ShouldBeInFastReceiptsMode(best);
                            best.IsInFullSync = ShouldBeInFullSyncMode(best);
                            best.IsInDisconnected = ShouldBeInDisconnectedMode(best);
                            best.IsInWaitingForBlock = ShouldBeInWaitingForBlockMode(best);

                            newModes = SyncMode.None;
                            CheckAddFlag(best.IsInUpdatingPivot, SyncMode.UpdatingPivot, ref newModes);
                            CheckAddFlag(best.IsInBeaconHeaders, SyncMode.BeaconHeaders, ref newModes);
                            CheckAddFlag(best.IsInFastHeaders, SyncMode.FastHeaders, ref newModes);
                            CheckAddFlag(best.IsInFastBodies, SyncMode.FastBodies, ref newModes);
                            CheckAddFlag(best.IsInFastReceipts, SyncMode.FastReceipts, ref newModes);
                            CheckAddFlag(best.IsInFastSync, SyncMode.FastSync, ref newModes);
                            CheckAddFlag(best.IsInFullSync, SyncMode.Full, ref newModes);
                            CheckAddFlag(best.IsInStateNodes, SyncMode.StateNodes, ref newModes);
                            CheckAddFlag(best.IsInSnapRanges, SyncMode.SnapSync, ref newModes);
                            CheckAddFlag(best.IsInDisconnected, SyncMode.Disconnected, ref newModes);
                            CheckAddFlag(best.IsInWaitingForBlock, SyncMode.WaitingForBlock, ref newModes);
                            SyncMode current = Current;
                            if (IsTheModeSwitchWorthMentioning(current, newModes))
                            {
                                if (_logger.IsInfo)
                                    _logger.Info($"Changing sync {current} to {newModes} at {BuildStateString(best)}");
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

        private static void CheckAddFlag(in bool flag, SyncMode mode, ref SyncMode resultMode)
        {
            if (flag)
            {
                resultMode |= mode;
            }
        }

        private bool IsTheModeSwitchWorthMentioning(SyncMode current, SyncMode newModes)
        {
            return newModes != current &&
                   (_logger.IsDebug ||
                   (newModes != SyncMode.WaitingForBlock || current != SyncMode.Full) &&
                   (newModes != SyncMode.Full || current != SyncMode.WaitingForBlock));
        }

        private void UpdateSyncModes(SyncMode newModes, string? reason = null)
        {
            if (_logger.IsTrace)
            {
                _logger.Trace($"Changing state to {newModes} | {reason}");
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
            $"pivot: {best.PivotNumber} | header: {best.Header} | header: {best.Header} | target: {best.TargetBlock} | peer: {best.Peer.Block} | state: {best.State}";

        private static string BuildStateStringDebug(Snapshot best) =>
            $"processed: {best.Processed} | state: {best.State} | block: {best.Block} | header: {best.Header} | chain difficulty: {best.ChainDifficulty} | target block: {best.TargetBlock} | peer block: {best.Peer.Block} | peer total difficulty: {best.Peer.TotalDifficulty}";

        private bool IsInAStickyFullSyncMode(Snapshot best)
        {
            long bestBlock = Math.Max(best.Processed, LastBlockThatEnabledFullSync ?? 0);
            bool hasEverBeenInFullSync = bestBlock > 0 && best.State > 0;
            long heightDelta = best.TargetBlock - bestBlock;
            return hasEverBeenInFullSync && heightDelta < FastSyncCatchUpHeightDelta;
        }

        private bool ShouldBeInWaitingForBlockMode(Snapshot best)
        {
            bool inBeaconControl = best.IsInBeaconControl;
            bool notInBeaconHeaders = !best.IsInBeaconHeaders;
            bool noDesiredPeerKnown = !AnyDesiredPeerKnown(best);
            bool postPivotPeerAvailable = best.AnyPostPivotPeerKnown;
            bool hasFastSyncBeenActive = best.Header >= best.PivotNumber;
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

        private bool ShouldBeInBeaconHeaders(bool shouldBeInUpdatingPivot)
        {
            bool shouldBeInBeaconHeaders = _beaconSyncStrategy.ShouldBeInBeaconHeaders();
            bool shouldBeNotInUpdatingPivot = !shouldBeInUpdatingPivot;

            bool result = shouldBeInBeaconHeaders &&
                          shouldBeNotInUpdatingPivot;

            if (_logger.IsTrace)
            {
                LogDetailedSyncModeChecks("BEACON HEADERS",
                    (nameof(shouldBeInBeaconHeaders), shouldBeInBeaconHeaders),
                    (nameof(shouldBeNotInUpdatingPivot), shouldBeNotInUpdatingPivot));
            }

            return result;
        }

        private bool ShouldBeInUpdatingPivot()
        {
            bool updateRequestedAndNotFinished = _syncConfig.MaxAttemptsToUpdatePivot > 0;
            bool isPostMerge = _beaconSyncStrategy.MergeTransitionFinished;
            bool stateSyncNotFinished = _syncProgressResolver.FindBestFullState() == 0;

            bool result = updateRequestedAndNotFinished &&
                          FastSyncEnabled &&
                          isPostMerge &&
                          stateSyncNotFinished;

            if (_logger.IsTrace)
            {
                LogDetailedSyncModeChecks("UPDATING PIVOT",
                    (nameof(updateRequestedAndNotFinished), updateRequestedAndNotFinished),
                    (nameof(FastSyncEnabled), FastSyncEnabled),
                    (nameof(isPostMerge), isPostMerge),
                    (nameof(stateSyncNotFinished), stateSyncNotFinished));
            }

            return result;
        }

        private bool ShouldBeInFastSyncMode(Snapshot best)
        {
            if (!FastSyncEnabled)
            {
                return false;
            }

            if (best.PivotNumber != 0 && best.Header == 0)
            {
                // do not start fast sync until at least one header is downloaded or we would start from zero
                // we are fine to start from zero if we do not use fast blocks
                return false;
            }

            bool notInUpdatingPivot = !best.IsInUpdatingPivot;

            // Shared with fast sync
            bool notInBeaconModes = !best.IsInAnyBeaconMode;
            bool postPivotPeerAvailable = best.AnyPostPivotPeerKnown;

            // We stop `FastSyncLag` block before the highest known block in case the highest known block is non-canon
            // and we need to sync away from it.
            // Note: its ok if target block height is not accurate as long as long full sync downloader does not stop
            //  earlier than this condition below which would cause a hang.
            bool notReachedFullSyncTransition = best.Header < best.TargetBlock - TotalSyncLag;

            bool notInAStickyFullSync = !IsInAStickyFullSyncMode(best);

            bool longRangeCatchUp = best.TargetBlock - best.State >= FastSyncCatchUpHeightDelta;
            bool stateNotDownloadedYet = !best.StateDownloaded;
            bool notNeedToWaitForHeaders = NotNeedToWaitForHeaders;

            bool result = notInUpdatingPivot &&
                          notInBeaconModes &&
                          postPivotPeerAvailable &&
                          // (catch up after node is off for a while
                          // OR standard fast sync)
                          notInAStickyFullSync &&
                          notReachedFullSyncTransition &&
                          (stateNotDownloadedYet || longRangeCatchUp) &&
                          notNeedToWaitForHeaders;

            if (_logger.IsTrace)
            {
                LogDetailedSyncModeChecks("FAST",
                    (nameof(notInUpdatingPivot), notInUpdatingPivot),
                    (nameof(notInBeaconModes), notInBeaconModes),
                    (nameof(postPivotPeerAvailable), postPivotPeerAvailable),
                    (nameof(notReachedFullSyncTransition), notReachedFullSyncTransition),
                    (nameof(notInAStickyFullSync), notInAStickyFullSync),
                    (nameof(stateNotDownloadedYet), stateNotDownloadedYet),
                    (nameof(longRangeCatchUp), longRangeCatchUp),
                    (nameof(notNeedToWaitForHeaders), notNeedToWaitForHeaders));
            }

            return result;
        }

        private bool ShouldBeInFullSyncMode(Snapshot best)
        {
            bool notInUpdatingPivot = !best.IsInUpdatingPivot;

            // Shared with fast sync
            bool notInBeaconModes = !best.IsInAnyBeaconMode;
            bool postPivotPeerAvailable = best.AnyPostPivotPeerKnown;

            // Shared with full sync archive
            bool desiredPeerKnown = AnyDesiredPeerKnown(best);

            // Full sync specific
            bool hasFastSyncBeenActive = best.Header >= best.PivotNumber;
            bool notInFastSync = !best.IsInFastSync;
            bool notInStateSync = !best.IsInStateSync;
            bool notNeedToWaitForHeaders = NotNeedToWaitForHeaders;

            bool result = notInUpdatingPivot &&
                          notInBeaconModes &&
                          desiredPeerKnown &&
                          postPivotPeerAvailable &&
                          hasFastSyncBeenActive &&
                          notInFastSync &&
                          notInStateSync &&
                          notNeedToWaitForHeaders;

            if (_logger.IsTrace)
            {
                LogDetailedSyncModeChecks("FULL",
                    (nameof(notInUpdatingPivot), notInUpdatingPivot),
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
            bool notInUpdatingPivot = !best.IsInUpdatingPivot;

            bool notInBeaconModes = !best.IsInAnyBeaconMode;
            bool desiredPeerKnown = AnyDesiredPeerKnown(best);

            bool result = notInUpdatingPivot &&
                          notInBeaconModes &&
                          desiredPeerKnown;

            if (_logger.IsTrace)
            {
                LogDetailedSyncModeChecks("FULL",
                    (nameof(notInUpdatingPivot), notInUpdatingPivot),
                    (nameof(notInBeaconModes), notInBeaconModes),
                    (nameof(desiredPeerKnown), desiredPeerKnown));
            }

            return result;
        }

        // ReSharper disable once UnusedParameter.Local
        private bool ShouldBeInFastHeadersMode(Snapshot best)
        {
            bool notInUpdatingPivot = !best.IsInUpdatingPivot;

            bool fastBlocksHeadersNotFinished = !FastBlocksHeadersFinished;

            if (_logger.IsTrace)
            {
                LogDetailedSyncModeChecks("HEADERS",
                    (nameof(notInUpdatingPivot), notInUpdatingPivot),
                    (nameof(fastBlocksHeadersNotFinished), fastBlocksHeadersNotFinished));
            }

            // this is really the only condition - fast blocks headers can always run if there are peers until it is done
            // also fast blocks headers can run in parallel with all other sync modes
            return notInUpdatingPivot &&
                   fastBlocksHeadersNotFinished;
        }

        private bool ShouldBeInFastBodiesMode(Snapshot best)
        {
            bool fastBodiesNotFinished = !FastBlocksBodiesFinished;
            bool fastHeadersFinished = FastBlocksHeadersFinished;
            bool notInStateSync = !best.IsInStateSync;
            bool stateSyncFinished = best.StateDownloaded;

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
            bool stateSyncFinished = best.StateDownloaded;

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

        private static bool ShouldBeInDisconnectedMode(Snapshot best)
        {
            return !best.IsInUpdatingPivot &&
                   !best.IsInFastBodies &&
                   !best.IsInFastHeaders &&
                   !best.IsInFastReceipts &&
                   !best.IsInFastSync &&
                   !best.IsInFullSync &&
                   !best.IsInStateSync &&
                   // maybe some more sophisticated heuristic?
                   best.Peer.TotalDifficulty.IsZero;
        }

        private bool ShouldBeInStateSyncMode(Snapshot best)
        {
            bool fastSyncEnabled = FastSyncEnabled;
            bool notInUpdatingPivot = !best.IsInUpdatingPivot;
            bool notInBeaconModes = !best.IsInAnyBeaconMode;
            bool hasFastSyncBeenActive = best.Header >= best.PivotNumber;
            bool hasAnyPostPivotPeer = best.AnyPostPivotPeerKnown;
            bool notInFastSync = !best.IsInFastSync;
            bool notNeedToWaitForHeaders = NotNeedToWaitForHeaders;
            bool stickyStateNodes = best.TargetBlock - best.Header < (_syncConfig.StateMinDistanceFromHead + StickyStateNodesDelta);

            bool longRangeCatchUp = best.TargetBlock - best.State >= FastSyncCatchUpHeightDelta;
            bool stateNotDownloadedYet = !best.StateDownloaded;

            bool notInAStickyFullSync = !IsInAStickyFullSyncMode(best);

            bool result = fastSyncEnabled &&
                          notInUpdatingPivot &&
                          notInBeaconModes &&
                          hasFastSyncBeenActive &&
                          hasAnyPostPivotPeer &&
                          (notInFastSync || stickyStateNodes) &&
                          (stateNotDownloadedYet || longRangeCatchUp) &&
                          notInAStickyFullSync &&
                          notNeedToWaitForHeaders;

            if (_logger.IsTrace)
            {
                LogDetailedSyncModeChecks("STATE",
                    (nameof(fastSyncEnabled), fastSyncEnabled),
                    (nameof(notInUpdatingPivot), notInUpdatingPivot),
                    (nameof(hasFastSyncBeenActive), hasFastSyncBeenActive),
                    (nameof(hasAnyPostPivotPeer), hasAnyPostPivotPeer),
                    ($"{nameof(notInFastSync)}||{nameof(stickyStateNodes)}", notInFastSync || stickyStateNodes),
                    (nameof(stateNotDownloadedYet), stateNotDownloadedYet),
                    (nameof(longRangeCatchUp), longRangeCatchUp),
                    (nameof(notInAStickyFullSync), notInAStickyFullSync),
                    (nameof(notNeedToWaitForHeaders), notNeedToWaitForHeaders));
            }

            return result;
        }

        private bool ShouldBeInStateNodesMode(Snapshot best)
        {
            bool isInStateSync = best.IsInStateSync;
            bool snapSyncDisabled = !SnapSyncEnabled;
            bool snapRangesFinished = _syncProgressResolver.IsSnapGetRangesFinished();

            bool result = isInStateSync && (snapSyncDisabled || snapRangesFinished);

            if (_logger.IsTrace)
            {
                LogDetailedSyncModeChecks("STATE_NODES",
                    (nameof(isInStateSync), isInStateSync),
                    ($"{nameof(snapSyncDisabled)}||{nameof(snapRangesFinished)}", snapSyncDisabled || snapRangesFinished));
            }

            return result;
        }

        private bool ShouldBeInSnapRangesPhase(Snapshot best)
        {
            bool isInStateSync = best.IsInStateSync;
            bool isCloseToHead = best.TargetBlock >= best.Header && (best.TargetBlock - best.Header) <= TotalSyncLag;
            bool snapNotFinished = !_syncProgressResolver.IsSnapGetRangesFinished();

            if (_logger.IsTrace)
            {
                LogDetailedSyncModeChecks("SNAP_RANGES",
                    (nameof(SnapSyncEnabled), SnapSyncEnabled),
                    (nameof(isInStateSync), isInStateSync),
                    (nameof(isCloseToHead), isCloseToHead),
                    (nameof(snapNotFinished), snapNotFinished));
            }

            return SnapSyncEnabled
                && isInStateSync
                && isCloseToHead
                && snapNotFinished;
        }

        private bool AnyDesiredPeerKnown(Snapshot best) => _betterPeerStrategy.IsDesiredPeer(best.Peer, (best.ChainDifficulty, best.Header));

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

        private Snapshot EnsureSnapshot(in UInt256 peerDifficulty, long peerBlock, bool inBeaconControl)
        {
            // need to find them in the reversed order otherwise we may fall behind the processing
            // and think that we have an invalid snapshot
            Snapshot best = TakeSnapshot(peerDifficulty, peerBlock, inBeaconControl);

            if (IsSnapshotInvalid(best))
            {
                string stateString = BuildStateStringDebug(best);
                if (_logger.IsWarn) _logger.Warn($"Invalid snapshot calculation: {stateString}. Recalculating progress pointers...");
                _syncProgressResolver.RecalculateProgressPointers();
                best = TakeSnapshot(peerDifficulty, peerBlock, inBeaconControl);
                if (IsSnapshotInvalid(best))
                {
                    string recalculatedSnapshot = BuildStateStringDebug(best);
                    string errorMessage = $"Cannot recalculate snapshot progress. Invalid snapshot calculation: {recalculatedSnapshot}";
                    if (_logger.IsError) _logger.Error(errorMessage);
                    throw new InvalidAsynchronousStateException(errorMessage);
                }
            }

            return best;
        }

        private Snapshot TakeSnapshot(in UInt256 peerDifficulty, long peerBlock, bool inBeaconControl)
        {
            // need to find them in the reversed order otherwise we may fall behind the processing
            // and think that we have an invalid snapshot
            long processed = _syncProgressResolver.FindBestProcessedBlock();
            long state = _syncProgressResolver.FindBestFullState();
            long block = _syncProgressResolver.FindBestFullBlock();
            long header = _syncProgressResolver.FindBestHeader();
            long targetBlock = _beaconSyncStrategy.GetTargetBlockHeight() ?? peerBlock;
            UInt256 chainDifficulty = _syncProgressResolver.ChainDifficulty;

            return new(processed, state, block, header, chainDifficulty, Math.Max(peerBlock, 0), peerDifficulty, inBeaconControl, targetBlock, _syncProgressResolver.SyncPivot.BlockNumber);
        }

        private static bool IsSnapshotInvalid(Snapshot best)
        {
            return // none of these values should ever be negative
                best.Block < 0
                || best.Header < 0
                || best.State < 0
                || best.Processed < 0
                || best.Peer.Block < 0
                || best.TargetBlock < 0
                // best header is at least equal to the best full block
                || best.Block > best.Header
                // we cannot download state for an unknown header
                || best.State > best.Header
                // we can only process blocks for which we have full body
                || best.Processed > best.Block;
            // for any processed block we should have its full state
            // but we only do limited lookups for state so we need to instead fast sync to now;
        }

        private void LogDetailedSyncModeChecks(string syncType, params (string Name, bool IsSatisfied)[] checks)
        {
            List<string> matched = new();
            List<string> failed = new();

            foreach ((string Name, bool IsSatisfied) in checks)
            {
                if (IsSatisfied)
                {
                    matched.Add(Name);
                }
                else
                {
                    failed.Add(Name);
                }
            }

            bool result = checks.All(static c => c.IsSatisfied);
            string text = $"{(result ? " * " : "   ")}{syncType,-20}: yes({string.Join(", ", matched)}), no({string.Join(", ", failed)})";
            _logger.Trace(text);
        }

        private ref struct Snapshot
        {
            public Snapshot(
                long processed,
                long state,
                long block,
                long header,
                UInt256 chainDifficulty,
                long peerBlock,
                in UInt256 peerDifficulty,
                bool isInBeaconControl,
                long targetBlock,
                long pivotNumber
            )
            {
                Processed = processed;
                State = state;
                Block = block;
                Header = header;
                ChainDifficulty = chainDifficulty;
                Peer = (peerDifficulty, peerBlock);
                IsInBeaconControl = isInBeaconControl;
                TargetBlock = targetBlock;
                PivotNumber = pivotNumber;

                IsInWaitingForBlock = IsInDisconnected = IsInFastReceipts = IsInFastBodies = IsInFastHeaders
                    = IsInFastSync = IsInFullSync = IsInStateSync = IsInStateNodes = IsInSnapRanges = IsInBeaconHeaders = IsInUpdatingPivot = false;
            }

            public bool IsInUpdatingPivot { get; set; }
            public bool IsInFastHeaders { get; set; }
            public bool IsInFastBodies { get; set; }
            public bool IsInFastReceipts { get; set; }
            public bool IsInFastSync { get; set; }
            public bool IsInStateSync { get; set; }
            public bool IsInStateNodes { get; set; }
            public bool IsInSnapRanges { get; set; }
            public bool IsInFullSync { get; set; }
            public bool IsInDisconnected { get; set; }
            public bool IsInWaitingForBlock { get; set; }
            public bool IsInBeaconHeaders { get; set; }
            public bool IsInBeaconControl { get; }
            public readonly bool IsInAnyBeaconMode => IsInBeaconHeaders || IsInBeaconControl;
            public readonly bool StateDownloaded => State >= PivotNumber;
            public readonly bool AnyPostPivotPeerKnown => Peer.Block > PivotNumber;

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
            /// The best block that we want to go to. best.Peer.Block for PoW, beaconSync.ProcessDestination for PoS,
            /// whith is the NewPayload/FCU block.
            /// </summary>
            public long TargetBlock { get; }

            /// <summary>
            /// Current difficulty of the chain
            /// </summary>
            public UInt256 ChainDifficulty { get; }

            /// <summary>
            /// Best peer block - this is what other peers are advertising - it may be lower than our best block if we get disconnected from best peers
            /// </summary>
            public (UInt256 TotalDifficulty, long Block) Peer { get; }

            public long PivotNumber { get; }

        }
    }
}
