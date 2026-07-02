// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Network.Contract.P2P;
using Nethermind.State.Snap;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Merge.Plugin.Synchronization;

public class StartingSyncPivotUpdater(
    IBlockTree blockTree,
    ISyncPeerPool syncPeerPool,
    ISyncConfig syncConfig,
    ISyncProgressResolver syncProgressResolver,
    IBlockCacheService blockCacheService,
    IBeaconSyncStrategy beaconSyncStrategy,
    ILogManager logManager) : ISyncPivotResolver, IDisposable
{
    private const string Pivot = "pivot";

    private readonly IBlockTree _blockTree = blockTree;
    private readonly ISyncPeerPool _syncPeerPool = syncPeerPool;
    private readonly ISyncConfig _syncConfig = syncConfig;
    private readonly ISyncProgressResolver _syncProgressResolver = syncProgressResolver;
    protected readonly IBlockCacheService _blockCacheService = blockCacheService;
    protected readonly IBeaconSyncStrategy _beaconSyncStrategy = beaconSyncStrategy;
    protected readonly ILogger _logger = logManager.GetClassLogger<StartingSyncPivotUpdater>();

    private CancellationTokenSource? _cancellation = new();

    // Note: Blocktree would have set MaxAttemptsToUpdatePivot to 0 if sync pivot is in DB
    private int _maxAttempts = syncConfig.MaxAttemptsToUpdatePivot;
    private int _attemptsLeft = syncConfig.MaxAttemptsToUpdatePivot;
    private Hash256 _alreadyAnnouncedNewPivotHash = Keccak.Zero;

    private DateTimeOffset _fastFillDeadline;
    private bool _fastFillAbandoned;
    private BlockHeader? _fastFillTarget;
    private (int SnapCapable, int Empty, int Timeouts) _fastFillProbeStats;

    private enum FastFillAttemptResult
    {
        PivotSet,
        KeepWaiting,
        GiveUp,
    }

    public async Task EnsureSyncPivot(CancellationToken cancellationToken)
    {
        CancellationTokenSource? cancellation = _cancellation;
        if (cancellation is null) return;

        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cancellation.Token);
        CancellationToken token = linkedCts.Token;
        TimeSpan retryDelay = TimeSpan.FromMilliseconds(_syncConfig.MultiSyncModeSelectorLoopTimerMs);

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!ShouldUpdatePivot())
                {
                    if (_logger.IsInfo) _logger.Info("Skipping pivot update");
                    return;
                }

                if (await TrySetFreshPivot(token))
                {
                    return;
                }

                // Mirrors the previous per-tick fallback: keep retrying while attempts remain (or forever
                // when infinite), otherwise give up and fall back to the pivot from the config file.
                if (!(_attemptsLeft-- > 0 || _maxAttempts == ISyncConfig.InfiniteAttempts))
                {
                    _syncConfig.MaxAttemptsToUpdatePivot = 0;
                    if (_logger.IsInfo) _logger.Info("Failed to update pivot block, skipping it and using pivot from config file.");
                    return;
                }

                await Task.Delay(retryDelay, token);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Unexpected error while updating the starting sync pivot.", e);
        }
    }

    // The negation of the former MultiSyncModeSelector.ShouldBeInUpdatingPivot check.
    private bool ShouldUpdatePivot() =>
        !_syncConfig.StaticSnapPivot &&
        _syncConfig.MaxAttemptsToUpdatePivot != 0 &&
        _syncConfig.FastSync &&
        _beaconSyncStrategy.MergeTransitionFinished &&
        _syncProgressResolver.FindBestFullState() == 0; // only resolve a fresh pivot when no state has been downloaded yet

    private async Task<bool> TrySetFreshPivot(CancellationToken cancellationToken)
    {
        (Hash256 Hash, ulong Number)? potentialPivotData = await TryGetPivotData(cancellationToken);

        if (potentialPivotData is null)
        {
            if (_logger.IsInfo && (_maxAttempts - _attemptsLeft) % 10 == 0) _logger.Info($"Waiting for Forkchoice message from Consensus Layer to set fresh pivot block [{_maxAttempts - _attemptsLeft}s]");
            return false;
        }

        if (PartialArchiveFastFillActive)
        {
            switch (await TrySetPartialArchiveFastFillPivot(potentialPivotData.Value.Number, cancellationToken))
            {
                case FastFillAttemptResult.PivotSet:
                    return true;
                case FastFillAttemptResult.KeepWaiting:
                    return false;
                case FastFillAttemptResult.GiveUp:
                    _fastFillAbandoned = true;
                    break;
            }
        }

        return TryOverwritePivot(potentialPivotData.Value.Hash, potentialPivotData.Value.Number);
    }

    private bool PartialArchiveFastFillActive =>
        !_fastFillAbandoned
        && _syncConfig.PartialArchiveEnabled
        && _syncConfig.PartialArchiveFastFillWaitMinutes > 0
        && _syncConfig.SnapSync
        && !_syncConfig.StaticSnapPivot;

    /// <summary>
    /// Attempts to pin the sync pivot <see cref="ISyncConfig.PartialArchiveRange"/> blocks behind
    /// the CL anchor so the whole historical window is filled at sync completion. Requires a peer
    /// (typically a configured feeder) able to serve snap state at that old root; falls back to
    /// the regular head pivot after <see cref="ISyncConfig.PartialArchiveFastFillWaitMinutes"/>.
    /// </summary>
    private async Task<FastFillAttemptResult> TrySetPartialArchiveFastFillPivot(ulong anchorNumber, CancellationToken cancellationToken)
    {
        ulong range = _syncConfig.PartialArchiveRange;
        if (anchorNumber <= range)
        {
            if (_logger.IsInfo) _logger.Info($"Partial archive fast fill: chain head ({anchorNumber}) is within the archive range ({range}); using the regular pivot.");
            return FastFillAttemptResult.GiveUp;
        }

        if (_fastFillDeadline == default)
        {
            _fastFillDeadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(_syncConfig.PartialArchiveFastFillWaitMinutes);
            if (_logger.IsInfo) _logger.Info($"Partial archive fast fill: looking for a peer serving snap state at block {anchorNumber - range} (head - {range}) for up to {_syncConfig.PartialArchiveFastFillWaitMinutes} minutes before falling back to forward filling.");
        }

        ulong targetNumber = anchorNumber - range;
        BlockHeader? target = _fastFillTarget;
        if (target is null || target.Number != targetNumber)
        {
            target = await TryGetFastFillTargetHeader(targetNumber, cancellationToken);
            _fastFillTarget = target;
        }

        if (target?.StateRoot is not null)
        {
            ISyncPeer? servingPeer = await TryFindPeerServingStateAt(target, cancellationToken);
            if (servingPeer is not null)
            {
                if (_logger.IsInfo) _logger.Info($"Partial archive fast fill: peer {servingPeer.Node?.ClientId} serves historical state at block {target.Number} ({target.Hash}); pinning it as a static snap pivot. The full {range}-block window will be available once forward sync reaches the head.");
                _syncConfig.PivotNumber = target.Number;
                _syncConfig.PivotHash = target.Hash!.ToString();
                _syncConfig.StaticSnapPivot = true;
                UpdateConfigValues(target.Hash!, target.Number);
                return FastFillAttemptResult.PivotSet;
            }
        }

        if (DateTimeOffset.UtcNow >= _fastFillDeadline)
        {
            if (_logger.IsWarn) _logger.Warn($"Partial archive fast fill: no peer served snap state at block {targetNumber} within {_syncConfig.PartialArchiveFastFillWaitMinutes} minutes; falling back to the regular head pivot. The historical window will fill forward, one block per slot.");
            return FastFillAttemptResult.GiveUp;
        }

        if (_logger.IsInfo && (_maxAttempts - _attemptsLeft) % 10 == 0)
        {
            TimeSpan left = _fastFillDeadline - DateTimeOffset.UtcNow;
            (int snapCapable, int empty, int timeouts) = _fastFillProbeStats;
            _logger.Info($"Partial archive fast fill: still probing for a peer serving state at block {targetNumber} (target header {(target is null ? "not resolved yet" : "resolved")}; {_syncPeerPool.InitializedPeersCount} peers, {snapCapable} snap-capable, last pass: {empty} without the state, {timeouts} timeouts; fallback in {left.TotalMinutes:F0}m).");
        }

        return FastFillAttemptResult.KeepWaiting;
    }

    /// <summary>
    /// Resolves the fast-fill target header by number, requiring two distinct peers to agree on
    /// the hash (a single peer is trusted only when it is the only one connected, e.g. a feeder).
    /// A wrong header cannot corrupt state — snap data is verified against its state root — but
    /// would waste the fast-fill attempt.
    /// </summary>
    private async Task<BlockHeader?> TryGetFastFillTargetHeader(ulong targetNumber, CancellationToken cancellationToken)
    {
        BlockHeader? candidate = null;
        int agreements = 0;
        int peersAsked = 0;

        foreach (PeerInfo peer in _syncPeerPool.InitializedPeers)
        {
            if (cancellationToken.IsCancellationRequested) return null;
            try
            {
                using IOwnedReadOnlyList<BlockHeader>? headers = await peer.SyncPeer.GetBlockHeaders(targetNumber, 1, 0, cancellationToken);
                peersAsked++;
                BlockHeader? header = headers is { Count: > 0 } ? headers[0] : null;
                if (header is null || header.Number != targetNumber || !HeaderValidator.ValidateHash(header)) continue;

                if (candidate is null)
                {
                    candidate = header;
                    agreements = 1;
                }
                else if (candidate.Hash == header.Hash)
                {
                    agreements++;
                }
                else
                {
                    if (_logger.IsWarn) _logger.Warn($"Partial archive fast fill: peers disagree on block {targetNumber} ({candidate.Hash} vs {header.Hash}); retrying.");
                    return null;
                }

                if (agreements >= 2) return candidate;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception e)
            {
                // Best-effort probe over untrusted peers: skip the peer, keep probing.
                if (_logger.IsDebug) _logger.Debug($"Partial archive fast fill: header request for {targetNumber} to {peer.SyncPeer.Node.ClientId} failed. {e.Message}");
            }
        }

        // With a single connected peer (e.g. only the feeder) accept its answer.
        return peersAsked == 1 && agreements == 1 ? candidate : null;
    }

    private async Task<ISyncPeer?> TryFindPeerServingStateAt(BlockHeader target, CancellationToken cancellationToken)
    {
        int snapCapable = 0;
        int empty = 0;
        int timeouts = 0;
        try
        {
            foreach (PeerInfo peer in _syncPeerPool.InitializedPeers)
            {
                if (cancellationToken.IsCancellationRequested) return null;
                if (!peer.SyncPeer.TryGetSatelliteProtocol(Protocol.Snap, out ISnapSyncPeer snapPeer)) continue;

                snapCapable++;
                try
                {
                    AccountRange probe = new(target.StateRoot!, ValueKeccak.Zero, ValueKeccak.Zero, target.Number);
                    using AccountsAndProofs response = await snapPeer.GetAccountRange(probe, cancellationToken);
                    if (response.PathAndAccounts.Count > 0 || response.Proofs.Count > 0)
                    {
                        return peer.SyncPeer;
                    }

                    empty++;
                    if (_logger.IsDebug) _logger.Debug($"Partial archive fast fill: peer {peer.SyncPeer.Node.ClientId} returned no data for state root {target.StateRoot} at block {target.Number}.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return null;
                }
                catch (Exception e)
                {
                    timeouts++;
                    if (_logger.IsDebug) _logger.Debug($"Partial archive fast fill: snap probe to {peer.SyncPeer.Node.ClientId} failed. {e.Message}");
                }
            }
        }
        finally
        {
            _fastFillProbeStats = (snapCapable, empty, timeouts);
        }

        return null;
    }

    protected virtual async Task<(Hash256 Hash, ulong Number)?> TryGetPivotData(CancellationToken cancellationToken)
    {
        // getting finalized block hash as it is safe, because can't be reorganized
        Hash256? finalizedBlockHash = _beaconSyncStrategy.GetFinalizedHash();

        if (finalizedBlockHash is not null && finalizedBlockHash != Keccak.Zero)
        {
            UpdateAndPrintPotentialNewPivot(finalizedBlockHash);

            ulong? finalizedBlockNumber = TryGetBlockNumberFromBlockCache(finalizedBlockHash)
                                         ?? TryGetFinalizedBlockNumberFromBlockTree(finalizedBlockHash)
                                         ?? await TryGetFromPeers(finalizedBlockHash, cancellationToken);

            return finalizedBlockNumber is null ? null : (finalizedBlockHash, finalizedBlockNumber.Value);
        }

        return null;
    }

    protected ulong? TryGetBlockNumberFromBlockCache(Hash256 finalizedBlockHash, string type = Pivot)
    {
        if (_logger.IsDebug) _logger.Debug($"Looking for {type} block in block cache");
        if (_blockCacheService.BlockCache.TryGetValue(finalizedBlockHash, out Block? finalizedBlock))
        {
            if (HeaderValidator.ValidateHash(finalizedBlock.Header))
            {
                if (_logger.IsDebug) _logger.Debug($"Found {type} block in block cache");
                return finalizedBlock.Header.Number;
            }
            if (_logger.IsDebug) _logger.Debug($"Hash of header found in block cache is {finalizedBlock.Header.Hash} when expecting {finalizedBlockHash}");
        }

        return null;
    }

    private ulong? TryGetFinalizedBlockNumberFromBlockTree(Hash256 finalizedBlockHash)
    {
        if (_logger.IsDebug) _logger.Debug("Looking for header of pivot block in blockTree");
        BlockHeader? finalizedHeader = _blockTree.FindHeader(finalizedBlockHash, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
        if (finalizedHeader is not null)
        {
            if (HeaderValidator.ValidateHash(finalizedHeader))
            {
                if (_logger.IsDebug) _logger.Debug("Found header of pivot block in block tree");
                return finalizedHeader.Number;
            }
            if (_logger.IsDebug) _logger.Debug($"Hash of header found in block tree is {finalizedHeader.Hash} when expecting {finalizedBlockHash}");
        }

        return null;
    }

    protected async Task<ulong?> TryGetFromPeers(Hash256? hash, CancellationToken cancellationToken, string type = Pivot) =>
        (await TryGetFromPeers(hash, cancellationToken, static async (peer, hash256, token) =>
        {
            BlockHeader? header = await peer.GetHeadBlockHeader(hash256, token);
            // Only accept a header that is actually the requested block; a peer must not substitute another.
            return header is not null && header.Hash == hash256 ? header : null;
        }, type))?.Number;

    protected async Task<BlockHeader?> TryGetFromPeers<T>(T id, CancellationToken cancellationToken,
        Func<ISyncPeer, T, CancellationToken, Task<BlockHeader?>> getHeader, string? type = Pivot)
    {
        foreach (PeerInfo peer in _syncPeerPool.InitializedPeers)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            try
            {
                if (_logger.IsInfo) _logger.Info($"Asking peer {peer.SyncPeer.Node.ClientId} for header of {type} block {id}");
                BlockHeader? finalizedHeader = await getHeader(peer.SyncPeer, id, cancellationToken);
                if (finalizedHeader is not null)
                {
                    if (HeaderValidator.ValidateHash(finalizedHeader))
                    {
                        if (_logger.IsInfo) _logger.Info($"Received header of {type} block from peer {peer.SyncPeer.Node.ClientId}");
                        return finalizedHeader;
                    }
                    if (_logger.IsInfo) _logger.Info($"Header of {type} block {id} from peer {peer.SyncPeer.Node.ClientId} failed hash validation");
                }
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                if (_logger.IsInfo) _logger.Info($"Peer {peer.SyncPeer.Node.ClientId} didn't respond to request for header of pivot block {id}");
                if (_logger.IsDebug) _logger.Debug($"Exception in GetHeadBlockHeader request to peer {peer.SyncPeer.Node.ClientId}. {exception}");
            }
        }

        if (type == Pivot && _logger.IsInfo && (_maxAttempts - _attemptsLeft) % 10 == 0)
        {
            _logger.Info($"Potential new pivot block: {id}. Waiting for pivot block header [{_maxAttempts - _attemptsLeft}s]");
        }

        return null;
    }

    private bool TryOverwritePivot(Hash256 potentialPivotBlockHash, ulong potentialPivotBlockNumber)
    {
        ulong targetBlock = _beaconSyncStrategy.GetTargetBlockHeight() ?? 0UL;
        bool isCloseToHead = targetBlock <= potentialPivotBlockNumber || (targetBlock - potentialPivotBlockNumber) < Constants.MaxDistanceFromHead;
        bool newPivotHigherThanOld = potentialPivotBlockNumber > _blockTree.SyncPivot.BlockNumber;

        if (isCloseToHead && newPivotHigherThanOld)
        {
            UpdateConfigValues(potentialPivotBlockHash, potentialPivotBlockNumber);

            if (_logger.IsInfo) _logger.Info($"New pivot block has been set based on ForkChoiceUpdate from CL. Pivot block number: {potentialPivotBlockNumber}, hash: {potentialPivotBlockHash}");
            return true;
        }

        if (!isCloseToHead && _logger.IsInfo) _logger.Info($"Pivot block from Consensus Layer too far from head. PivotBlockNumber: {potentialPivotBlockNumber}, TargetBlockNumber: {targetBlock}, difference: {targetBlock - potentialPivotBlockNumber} blocks. Max difference allowed: {Constants.MaxDistanceFromHead}");
        if (!newPivotHigherThanOld && _logger.IsInfo) _logger.Info($"Pivot block from Consensus Layer isn't higher than pivot from initial config. New PivotBlockNumber: {potentialPivotBlockNumber}, old: {_syncConfig.PivotNumber}");
        return false;
    }

    private void UpdateConfigValues(Hash256 finalizedBlockHash, ulong finalizedBlockNumber)
    {
        _blockTree.SyncPivot = (finalizedBlockNumber, finalizedBlockHash);
        _syncConfig.MaxAttemptsToUpdatePivot = 0;
    }

    protected void UpdateAndPrintPotentialNewPivot(Hash256 finalizedBlockHash)
    {
        if (_alreadyAnnouncedNewPivotHash != finalizedBlockHash)
        {
            if (_logger.IsInfo) _logger.Info($"Potential new pivot block hash: {finalizedBlockHash}");
            _alreadyAnnouncedNewPivotHash = finalizedBlockHash;
        }
    }

    public void Dispose() => CancellationTokenExtensions.CancelDisposeAndClear(ref _cancellation);
}
