// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
    ILogManager logManager,
    TimeProvider? timeProvider = null) : ISyncPivotResolver, IDisposable
{
    private const string Pivot = "pivot";
    private const ulong DefaultMaxStoredPivotAncestorWalk = 65_536;
    private const ulong LocalAncestryCheckpointInterval = 256;
    private const int MaxAncestryCheckpoints = 128;
    private static readonly TimeSpan PreferredAncestryBatchLatency = TimeSpan.FromSeconds(2);

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
    private bool _storedPivotChecked;
    private Hash256? _storedPivotToVerify;

    /// <summary>Whether a pivot stored by an earlier run is resolved again while nothing has been synced from it.</summary>
    protected virtual bool ReverifiesStoredPivot => false;

    /// <summary>
    /// The most headers read, from the block tree or from peers, to find the stored pivot below the verified pivot. A
    /// stored pivot further below is replaced, which costs a new header sync from the verified pivot.
    /// </summary>
    internal ulong MaxStoredPivotAncestorWalk { get; init; } = DefaultMaxStoredPivotAncestorWalk;

    internal TimeProvider AncestryTimeProvider { get; init; } = timeProvider ?? TimeProvider.System;

    /// <summary>The most headers asked of a peer in one request of the walk to the stored pivot.</summary>
    internal int AncestorHeadersPerRequest { get; init; } = 192;

    /// <summary>
    /// The longest time one attempt walks to the stored pivot. A walk that takes longer leaves the stored pivot
    /// undecided; the next attempt continues from where it stopped.
    /// </summary>
    internal TimeSpan MaxStoredPivotWalkTime { get; init; } = TimeSpan.FromMinutes(5);

    private readonly HashSet<PeerInfo> _partialAncestryPeers = [];
    private readonly SortedList<ulong, WalkProgress> _walkProgress = [];
    private readonly Dictionary<PeerInfo, TimeSpan> _ancestryBatchLatency = [];
    private readonly List<PeerInfo> _pendingAncestryPeers = [];
    private int _undecidedChecks;
    private int _pivotCheckErrors;

    /// <summary>
    /// How far an earlier attempt walked: <see cref="Below"/> is the next block to read below
    /// <see cref="Pivot"/>, and every block between them is proven to be on its chain.
    /// </summary>
    private readonly record struct WalkProgress((Hash256 Hash, ulong Number) Pivot, (Hash256 Hash, ulong Number) Below);

    /// <summary>The result of checking a stored pivot against the verified pivot.</summary>
    internal enum StoredPivotCheck
    {
        /// <summary>The stored pivot is the verified pivot or one of its ancestors.</summary>
        OnVerifiedChain,

        /// <summary>A different block is proven at the stored number, or the stored pivot is too far below.</summary>
        NotOnVerifiedChain,

        /// <summary>No peer served the missing ancestors; the check is done again later.</summary>
        Undecided,
    }

    public async Task EnsureSyncPivot(CancellationToken cancellationToken)
    {
        CancellationTokenSource? cancellation = _cancellation;
        if (cancellation is null) return;

        VerifyStoredPivotAgain();

        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cancellation.Token);
        CancellationToken token = linkedCts.Token;
        TimeSpan retryDelay = TimeSpan.FromMilliseconds(_syncConfig.MultiSyncModeSelectorLoopTimerMs);

        try
        {
            while (!token.IsCancellationRequested)
            {
                try
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
                }
                catch (Exception e) when (!token.IsCancellationRequested)
                {
                    bool expected = e is OperationCanceledException or ObjectDisposedException;
                    if (_storedPivotToVerify is null)
                    {
                        if (!expected && _logger.IsError) _logger.Error("Unexpected error while updating the starting sync pivot.", e);
                        return;
                    }

                    if (_logger.IsError && _pivotCheckErrors++ % 10 == 0)
                    {
                        if (_pivotCheckErrors == 1) _logger.Error("Unexpected error while checking the sync pivot from db; trying again.", e);
                        else _logger.Error($"Unexpected error while checking the sync pivot from db, {_pivotCheckErrors} so far; trying again: {e.Message}");
                    }
                }

                await Task.Delay(retryDelay, token);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <remarks>
    /// A pivot stored by an earlier run disables the updater. An older version took that pivot from a single peer header,
    /// so it can name a block that is not on the chain, and the node then never syncs. While no state is downloaded, the
    /// pivot is resolved again from the CL head; the stored one is kept only when it is on the verified chain.
    /// </remarks>
    private void VerifyStoredPivotAgain()
    {
        if (_storedPivotChecked || !ReverifiesStoredPivot) return;
        _storedPivotChecked = true;

        if (!_blockTree.StoredPivotDisabledUpdater || _syncProgressResolver.FindBestFullState() != 0) return;

        (ulong number, Hash256 hash) = _blockTree.SyncPivot;
        if (_logger.IsInfo) _logger.Info($"Pivot block {number} ({hash}) from db has no synced state yet; checking it against the Consensus Layer head.");
        _storedPivotToVerify = hash;
        _maxAttempts = _attemptsLeft = ISyncConfig.InfiniteAttempts;
        _syncConfig.MaxAttemptsToUpdatePivot = ISyncConfig.InfiniteAttempts;
    }

    /// <summary>Checks whether the stored pivot is the verified pivot or one of its ancestors.</summary>
    /// <remarks>
    /// The walk follows parent hashes down from the verified pivot. It reads headers from the block tree and, below the
    /// first header that is not stored, asks peers for batches of headers in reverse order. Each header must have the
    /// expected hash and number, so a peer cannot lead the walk off the verified chain. A pivot more than
    /// <see cref="MaxStoredPivotAncestorWalk"/> blocks below the verified pivot is not walked to.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> is canceled during the walk.</exception>
    internal async Task<StoredPivotCheck> CheckStoredPivot(Hash256 storedHash, ulong storedNumber, Hash256 verifiedHash, ulong verifiedNumber, CancellationToken cancellationToken)
    {
        if (storedNumber > verifiedNumber || verifiedNumber - storedNumber > MaxStoredPivotAncestorWalk) return StoredPivotCheck.NotOnVerifiedChain;

        using CancellationTokenSource walkTimeout = new(MaxStoredPivotWalkTime, AncestryTimeProvider);
        using CancellationTokenSource walkDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, walkTimeout.Token);
        Hash256 hash = verifiedHash;
        ulong number = verifiedNumber;
        try
        {
            while (number > storedNumber)
            {
                walkDeadline.Token.ThrowIfCancellationRequested();
                if (_walkProgress.TryGetValue(number, out WalkProgress progress) && progress.Pivot.Hash == hash
                    && progress.Below.Number >= storedNumber)
                {
                    _walkProgress.Remove(number);
                    (hash, number) = progress.Below;
                    Checkpoint();
                    continue;
                }

                BlockHeader? header = _blockTree.FindHeader(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded, number);
                if (header is not null)
                {
                    if (header.ParentHash is not { } parentHash) return StoredPivotCheck.NotOnVerifiedChain;
                    hash = parentHash;
                    number--;
                    if (number % LocalAncestryCheckpointInterval == 0) Checkpoint();
                    continue;
                }

                ulong gap = number - storedNumber;
                for (int i = _walkProgress.Count - 1; i >= 0; i--)
                {
                    ulong anchor = _walkProgress.Keys[i];
                    if (anchor >= number) continue;
                    if (anchor <= storedNumber) break;
                    if (_walkProgress.Values[i].Below.Number < storedNumber) continue;
                    gap = number - anchor;
                    break;
                }

                int count = (int)Math.Min(gap, (ulong)AncestorHeadersPerRequest);
                (Hash256 Hash, ulong Number)? next = await WalkPeerHeaders(hash, number, count, walkDeadline.Token);
                cancellationToken.ThrowIfCancellationRequested();
                if (next is null)
                {
                    Checkpoint();
                    return StoredPivotCheck.Undecided;
                }
                (hash, number) = next.Value;
                Checkpoint();
            }
        }
        catch (OperationCanceledException) when (walkTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Checkpoint();
            if (_logger.IsInfo) _logger.Info($"Pivot block {storedNumber} from db is not checked yet: the walk to it took longer than {MaxStoredPivotWalkTime}; continuing later from block {number}.");
            return StoredPivotCheck.Undecided;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return hash == storedHash ? StoredPivotCheck.OnVerifiedChain : StoredPivotCheck.NotOnVerifiedChain;

        void Checkpoint()
        {
            if (number == verifiedNumber) return;
            if (_walkProgress.Count == MaxAncestryCheckpoints && !_walkProgress.ContainsKey(verifiedNumber))
            {
                int last = _walkProgress.Count - 1;
                _walkProgress.RemoveAt(_walkProgress.Keys[last] > verifiedNumber ? last : 0);
            }
            _walkProgress[verifiedNumber] = new WalkProgress((verifiedHash, verifiedNumber), (hash, number));
        }
    }

    /// <summary>
    /// Drops disconnected peers and favors those that served a full batch. Among the other peers, those still waiting
    /// from the previous request go first. A retry can continue after a deadline even as new peers join.
    /// </summary>
    private List<PeerInfo> PeersForAncestry()
    {
        List<PeerInfo> peers = [];
        List<PeerInfo> fallback = [];
        HashSet<PeerInfo> live = [];
        foreach (PeerInfo peer in _syncPeerPool.InitializedPeers)
        {
            live.Add(peer);
            if (ServedFullAncestryBatch(peer)) peers.Add(peer);
            else fallback.Add(peer);
        }

        _partialAncestryPeers.IntersectWith(live);
        foreach (PeerInfo peer in _ancestryBatchLatency.Keys)
        {
            if (!live.Contains(peer)) _ancestryBatchLatency.Remove(peer);
        }

        _pendingAncestryPeers.RemoveAll(peer => !live.Contains(peer));
        HashSet<PeerInfo> waiting = [.. _pendingAncestryPeers];
        foreach (PeerInfo peer in fallback)
        {
            if (waiting.Add(peer)) _pendingAncestryPeers.Add(peer);
        }

        peers.Sort((x, y) => _ancestryBatchLatency[x].CompareTo(_ancestryBatchLatency[y]));
        int slowPeer = peers.FindIndex(peer => _ancestryBatchLatency[peer] > PreferredAncestryBatchLatency);
        if (slowPeer >= 0 && _pendingAncestryPeers.Count > 0)
        {
            peers.Insert(slowPeer, _pendingAncestryPeers[0]);
            for (int i = 1; i < _pendingAncestryPeers.Count; i++) peers.Add(_pendingAncestryPeers[i]);
        }
        else
        {
            peers.AddRange(_pendingAncestryPeers);
        }

        return peers;
    }

    private bool ServedFullAncestryBatch(PeerInfo peer) =>
        !_partialAncestryPeers.Contains(peer) && _ancestryBatchLatency.ContainsKey(peer);

    internal int AncestryPeerScores => _partialAncestryPeers.Count + _ancestryBatchLatency.Count;

    /// <summary>Follows parent hashes through up to <paramref name="count"/> headers that a peer serves from <paramref name="hash"/> down.</summary>
    /// <remarks>
    /// A partial batch is used when no peer returns a full one before the deadline.
    /// </remarks>
    /// <returns>
    /// The hash and number below the last header that links up, or null when no peer served a linked header. When
    /// <paramref name="cancellationToken"/> is canceled, the linked part served so far is returned.
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled before a peer served a linked header.</exception>
    private async Task<(Hash256 Hash, ulong Number)?> WalkPeerHeaders(Hash256 hash, ulong number, int count, CancellationToken cancellationToken)
    {
        (Hash256 Hash, ulong Number)? partialBatch = null;
        foreach (PeerInfo peer in PeersForAncestry())
        {
            if (cancellationToken.IsCancellationRequested) break;
            _pendingAncestryPeers.Remove(peer);
            using CancellationTokenSource timeout = new(Timeouts.Eth, AncestryTimeProvider);
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            long started = AncestryTimeProvider.GetTimestamp();
            try
            {
                using IOwnedReadOnlyList<BlockHeader>? headers = await RequestAncestry(peer.SyncPeer, hash, count, deadline.Token);
                Hash256 expectedHash = hash;
                ulong expectedNumber = number;
                for (int i = 0; i < Math.Min(headers?.Count ?? 0, count); i++)
                {
                    BlockHeader header = headers![i];
                    if (header.Number != expectedNumber || header.Hash != expectedHash
                        || !HeaderValidator.ValidateHash(header) || header.ParentHash is null) break;
                    expectedHash = header.ParentHash;
                    expectedNumber--;
                }

                ulong linked = number - expectedNumber;
                if (linked == (ulong)count)
                {
                    _ancestryBatchLatency[peer] = AncestryTimeProvider.GetElapsedTime(started);
                    _partialAncestryPeers.Remove(peer);
                    return (expectedHash, expectedNumber);
                }

                _partialAncestryPeers.Add(peer);
                if (linked > 0 && (partialBatch is null || expectedNumber < partialBatch.Value.Number))
                {
                    partialBatch = (expectedHash, expectedNumber);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                if (_logger.IsDebug) _logger.Debug($"Peer {peer} did not serve the ancestors of {hash}: {exception}");
                _partialAncestryPeers.Add(peer);
            }
        }

        if (partialBatch is null) cancellationToken.ThrowIfCancellationRequested();
        return partialBatch;
    }

    protected async Task<IOwnedReadOnlyList<BlockHeader>?> RequestAncestry(ISyncPeer peer, Hash256 hash, int count, CancellationToken cancellationToken)
    {
        Task<IOwnedReadOnlyList<BlockHeader>?> request = peer.GetAncestorHeaders(hash, count, cancellationToken);
        try
        {
            return await request.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _ = DisposeAbandonedHeaders(request, _logger);
            throw;
        }
    }

    private static async Task DisposeAbandonedHeaders(Task<IOwnedReadOnlyList<BlockHeader>?> request, ILogger logger)
    {
        try
        {
            using IOwnedReadOnlyList<BlockHeader>? headers = await request.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (logger.IsDebug) logger.Debug($"Abandoned pivot ancestry request completed with an error: {exception}");
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
            if (_logger.IsInfo && (_maxAttempts - _attemptsLeft) % 10 == 0)
            {
                Hash256? finalizedBlockHash = _beaconSyncStrategy.GetFinalizedHash();
                if (finalizedBlockHash is null || finalizedBlockHash == Keccak.Zero)
                {
                    _logger.Info($"Waiting for Forkchoice message from Consensus Layer to set fresh pivot block [{_maxAttempts - _attemptsLeft}s]");
                }
                else
                {
                    _logger.Info($"Known finalized block hash {finalizedBlockHash}, but cannot resolve its block number yet ({_syncPeerPool.InitializedPeersCount} peers available) [{_maxAttempts - _attemptsLeft}s]");
                }
            }
            return false;
        }

        return await TryOverwritePivot(potentialPivotData.Value.Hash, potentialPivotData.Value.Number, cancellationToken);
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
        (await TryGetFromPeers(hash, cancellationToken,
            static (peer, hash256, token) => peer.GetHeadBlockHeader(hash256, token), type))?.Number;

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

    private async Task<bool> TryOverwritePivot(Hash256 potentialPivotBlockHash, ulong potentialPivotBlockNumber, CancellationToken cancellationToken)
    {
        ulong targetBlock = _beaconSyncStrategy.GetTargetBlockHeight() ?? 0UL;
        bool isCloseToHead = targetBlock <= potentialPivotBlockNumber || (targetBlock - potentialPivotBlockNumber) < Constants.MaxDistanceFromHead;
        if (isCloseToHead && _storedPivotToVerify is { } storedHash)
        {
            ulong storedNumber = _blockTree.SyncPivot.BlockNumber;
            StoredPivotCheck check = await CheckStoredPivot(storedHash, storedNumber, potentialPivotBlockHash, potentialPivotBlockNumber, cancellationToken);
            if (check == StoredPivotCheck.Undecided)
            {
                if (_logger.IsInfo && _undecidedChecks++ % 10 == 0) _logger.Info($"Pivot block {storedNumber} from db is not checked yet: no peer served the headers below {potentialPivotBlockNumber}; trying again.");
                return false;
            }

            if (check == StoredPivotCheck.OnVerifiedChain)
            {
                _syncConfig.MaxAttemptsToUpdatePivot = 0;
                if (_logger.IsInfo) _logger.Info($"Pivot block {storedNumber} from db is on the chain of the Consensus Layer head; keeping it.");
            }
            else
            {
                if (_logger.IsWarn) _logger.Warn($"Pivot block {storedNumber} ({storedHash}) from db is not on the chain of the Consensus Layer head, or more than {MaxStoredPivotAncestorWalk} blocks below its pivot; replacing it with {potentialPivotBlockNumber} ({potentialPivotBlockHash}) and syncing headers again from there.");
                _blockTree.ReplaceSyncPivot((potentialPivotBlockNumber, potentialPivotBlockHash));
                _syncConfig.MaxAttemptsToUpdatePivot = 0;
            }

            _storedPivotToVerify = null;
            _walkProgress.Clear();
            _partialAncestryPeers.Clear();
            _ancestryBatchLatency.Clear();
            _pendingAncestryPeers.Clear();
            return true;
        }

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
