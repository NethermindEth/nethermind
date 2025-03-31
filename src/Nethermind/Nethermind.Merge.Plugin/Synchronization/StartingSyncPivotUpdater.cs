// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.State.Snap;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Merge.Plugin.Synchronization;

public class StartingSyncPivotUpdater
{
    private const string Pivot = "pivot";
    private readonly IBlockTree _blockTree;
    private readonly ISyncModeSelector _syncModeSelector;
    private readonly ISyncPeerPool _syncPeerPool;
    private readonly ISyncConfig _syncConfig;
    protected readonly IBlockCacheService _blockCacheService;
    protected readonly IBeaconSyncStrategy _beaconSyncStrategy;
    protected readonly ILogger _logger;

    private readonly CancellationTokenSource _cancellation = new();

    private static int _maxAttempts;
    private int _attemptsLeft;
    private int _updateInProgress;
    private Hash256 _alreadyAnnouncedNewPivotHash = Keccak.Zero;

    public StartingSyncPivotUpdater(IBlockTree blockTree,
        ISyncModeSelector syncModeSelector,
        ISyncPeerPool syncPeerPool,
        ISyncConfig syncConfig,
        IBlockCacheService blockCacheService,
        IBeaconSyncStrategy beaconSyncStrategy,
        ILogManager logManager)
    {
        _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        _syncModeSelector = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
        _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
        _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
        _blockCacheService = blockCacheService ?? throw new ArgumentNullException(nameof(blockCacheService));
        _beaconSyncStrategy = beaconSyncStrategy ?? throw new ArgumentNullException(nameof(beaconSyncStrategy));
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

        _maxAttempts = syncConfig.MaxAttemptsToUpdatePivot; // Note: Blocktree would have set this to 0 if sync pivot is in DB
        _attemptsLeft = syncConfig.MaxAttemptsToUpdatePivot;

        if (_maxAttempts == 0)
        {
            _beaconSyncStrategy.AllowBeaconHeaderSync();
        }
        else
        {
            _syncModeSelector.Changed += OnSyncModeChanged;
        }
    }

    private async void OnSyncModeChanged(object? sender, SyncModeChangedEventArgs syncMode)
    {
        if ((syncMode.Current & SyncMode.UpdatingPivot) != 0 && Interlocked.CompareExchange(ref _updateInProgress, 1, 0) == 0)
        {
            if (await TrySetFreshPivot(_cancellation.Token))
            {
                _syncModeSelector.Changed -= OnSyncModeChanged;
            }
            else if (_attemptsLeft-- > 0)
            {
                Interlocked.CompareExchange(ref _updateInProgress, 0, 1);
            }
            else
            {
                _syncModeSelector.Changed -= OnSyncModeChanged;
                _syncConfig.MaxAttemptsToUpdatePivot = 0;
                _beaconSyncStrategy.AllowBeaconHeaderSync();
                if (_logger.IsInfo) _logger.Info("Failed to update pivot block, skipping it and using pivot from config file.");
            }
        }

        // if sync mode is different than UpdatePivot, it means it will never be in UpdatePivot
        if ((syncMode.Current & SyncMode.UpdatingPivot) == 0)
        {
            _syncModeSelector.Changed -= OnSyncModeChanged;
            _syncConfig.MaxAttemptsToUpdatePivot = 0;
            _beaconSyncStrategy.AllowBeaconHeaderSync();
            if (_logger.IsInfo) _logger.Info("Skipping pivot update");
        }
    }

    private async Task<bool> TrySetFreshPivot(CancellationToken cancellationToken)
    {
        (Hash256 Hash, long Number)? potentialPivotData = await TryGetPivotData(cancellationToken);

        if (potentialPivotData is null)
        {
            if (_logger.IsInfo && (_maxAttempts - _attemptsLeft) % 10 == 0) _logger.Info($"Waiting for Forkchoice message from Consensus Layer to set fresh pivot block [{_maxAttempts - _attemptsLeft}s]");
            return false;
        }

        return TryOverwritePivot(potentialPivotData.Value.Hash, potentialPivotData.Value.Number);
    }

    protected virtual async Task<(Hash256 Hash, long Number)?> TryGetPivotData(CancellationToken cancellationToken)
    {
        // getting finalized block hash as it is safe, because can't be reorganized
        Hash256? finalizedBlockHash = _beaconSyncStrategy.GetFinalizedHash();

        if (finalizedBlockHash is not null && finalizedBlockHash != Keccak.Zero)
        {
            UpdateAndPrintPotentialNewPivot(finalizedBlockHash);

            long? finalizedBlockNumber = TryGetBlockNumberFromBlockCache(finalizedBlockHash)
                                         ?? TryGetFinalizedBlockNumberFromBlockTree(finalizedBlockHash)
                                         ?? await TryGetFromPeers(finalizedBlockHash, cancellationToken);

            return finalizedBlockNumber is null ? null : (finalizedBlockHash, (long)finalizedBlockNumber);
        }

        return null;
    }

    protected long? TryGetBlockNumberFromBlockCache(Hash256 finalizedBlockHash, string type = Pivot)
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

    private long? TryGetFinalizedBlockNumberFromBlockTree(Hash256 finalizedBlockHash)
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

    protected async Task<long?> TryGetFromPeers(Hash256? hash, CancellationToken cancellationToken, string type = Pivot) =>
        (await TryGetFromPeers(hash, cancellationToken, static (peer, hash256, token) => peer.GetHeadBlockHeader(hash256, token), type))?.Number;

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
                    if (_logger.IsInfo) _logger.Info($"Hash of header received from peer {peer.SyncPeer.Node.ClientId} is {finalizedHeader.Hash} when expecting {id}");
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

    private bool TryOverwritePivot(Hash256 potentialPivotBlockHash, long potentialPivotBlockNumber)
    {
        long targetBlock = _beaconSyncStrategy.GetTargetBlockHeight() ?? 0;
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

    private void UpdateConfigValues(Hash256 finalizedBlockHash, long finalizedBlockNumber)
    {
        _blockTree.SyncPivot = (finalizedBlockNumber, finalizedBlockHash);
        _syncConfig.MaxAttemptsToUpdatePivot = 0;
        _beaconSyncStrategy.AllowBeaconHeaderSync();
    }

    protected void UpdateAndPrintPotentialNewPivot(Hash256 finalizedBlockHash)
    {
        if (_alreadyAnnouncedNewPivotHash != finalizedBlockHash)
        {
            if (_logger.IsInfo) _logger.Info($"Potential new pivot block hash: {finalizedBlockHash}");
            _alreadyAnnouncedNewPivotHash = finalizedBlockHash;
        }
    }
}
