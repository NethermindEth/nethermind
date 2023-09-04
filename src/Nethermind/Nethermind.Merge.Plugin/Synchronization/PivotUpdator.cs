// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Merge.Plugin.Synchronization;

public class PivotUpdator
{
    private readonly IBlockTree _blockTree;
    private readonly ISyncModeSelector _syncModeSelector;
    private readonly ISyncPeerPool _syncPeerPool;
    private readonly ISyncConfig _syncConfig;
    private readonly IBlockCacheService _blockCacheService;
    private readonly IBeaconSyncStrategy _beaconSyncStrategy;
    private readonly IDb _metadataDb;
    private readonly ILogger _logger;

    private readonly CancellationTokenSource _cancellation = new();

    private static int _maxAttempts;
    private int _attemptsLeft;
    private int _updateInProgress;
    private Keccak _alreadyAnnouncedNewPivotHash = Keccak.Zero;

    public PivotUpdator(IBlockTree blockTree,
        ISyncModeSelector syncModeSelector,
        ISyncPeerPool syncPeerPool,
        ISyncConfig syncConfig,
        IBlockCacheService blockCacheService,
        IBeaconSyncStrategy beaconSyncStrategy,
        IDb metadataDb,
        ILogManager logManager)
    {
        _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        _syncModeSelector = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
        _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
        _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
        _blockCacheService = blockCacheService ?? throw new ArgumentNullException(nameof(blockCacheService));
        _beaconSyncStrategy = beaconSyncStrategy ?? throw new ArgumentNullException(nameof(beaconSyncStrategy));
        _metadataDb = metadataDb ?? throw new ArgumentNullException(nameof(metadataDb));
        _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

        _maxAttempts = syncConfig.MaxAttemptsToUpdatePivot;
        _attemptsLeft = syncConfig.MaxAttemptsToUpdatePivot;

        if (!TryUpdateSyncConfigUsingDataFromDb())
        {
            _syncModeSelector.Changed += OnSyncModeChanged;
        }
    }

    private bool TryUpdateSyncConfigUsingDataFromDb()
    {
        try
        {
            if (_metadataDb.KeyExists(MetadataDbKeys.UpdatedPivotData))
            {
                byte[]? pivotFromDb = _metadataDb.Get(MetadataDbKeys.UpdatedPivotData);
                RlpStream pivotStream = new(pivotFromDb!);
                long updatedPivotBlockNumber = pivotStream.DecodeLong();
                Keccak updatedPivotBlockHash = pivotStream.DecodeKeccak()!;

                _syncConfig.PivotNumber = updatedPivotBlockNumber.ToString();
                _syncConfig.PivotHash = updatedPivotBlockHash.ToString();
                _syncConfig.MaxAttemptsToUpdatePivot = 0;

                if (_logger.IsInfo) _logger.Info($"Pivot block has been set based on data from db. Pivot block number: {updatedPivotBlockNumber}, hash: {updatedPivotBlockHash}");
                return true;
            }
        }
        catch (RlpException)
        {
            if (_logger.IsWarn) _logger.Warn($"Cannot decode pivot block number or hash");
        }

        return false;
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
                if (_logger.IsInfo) _logger.Info("Failed to update pivot block, skipping it and using pivot from config file.");
            }
        }

        // if sync mode is different than UpdatePivot, it means it will never be in UpdatePivot
        if ((syncMode.Current & SyncMode.UpdatingPivot) == 0)
        {
            _syncModeSelector.Changed -= OnSyncModeChanged;
            _syncConfig.MaxAttemptsToUpdatePivot = 0;
            if (_logger.IsInfo) _logger.Info("Skipping pivot update");
        }
    }

    private async Task<bool> TrySetFreshPivot(CancellationToken cancellationToken)
    {
        Keccak? finalizedBlockHash = TryGetFinalizedBlockHashFromCl();

        if (finalizedBlockHash is null || finalizedBlockHash == Keccak.Zero)
        {
            return false;
        }

        long? finalizedBlockNumber = TryGetFinalizedBlockNumberFromBlockCache(finalizedBlockHash);
        finalizedBlockNumber ??= TryGetFinalizedBlockNumberFromBlockTree(finalizedBlockHash);
        finalizedBlockNumber ??= await TryGetFinalizedBlockNumberFromPeers(finalizedBlockHash, cancellationToken);

        return finalizedBlockNumber is not null && TryOverwritePivot(finalizedBlockHash, (long)finalizedBlockNumber);
    }

    private Keccak? TryGetFinalizedBlockHashFromCl()
    {
        Keccak? finalizedBlockHash = _beaconSyncStrategy.GetFinalizedHash();

        if (finalizedBlockHash is null || finalizedBlockHash == Keccak.Zero)
        {
            if (_logger.IsInfo && (_maxAttempts - _attemptsLeft) % 10 == 0) _logger.Info($"Waiting for Forkchoice message from Consensus Layer to set fresh pivot block [{_maxAttempts - _attemptsLeft}s]");

            return null;
        }

        if (_alreadyAnnouncedNewPivotHash != finalizedBlockHash)
        {
            if (_logger.IsInfo) _logger.Info($"Potential new pivot block hash: {finalizedBlockHash}");
            _alreadyAnnouncedNewPivotHash = finalizedBlockHash;
        }

        return finalizedBlockHash;
    }

    private long? TryGetFinalizedBlockNumberFromBlockCache(Keccak finalizedBlockHash)
    {
        if (_logger.IsDebug) _logger.Debug("Looking for pivot block in block cache");
        if (_blockCacheService.BlockCache.TryGetValue(finalizedBlockHash, out Block? finalizedBlock))
        {
            if (HeaderValidator.ValidateHash(finalizedBlock.Header))
            {
                if (_logger.IsDebug) _logger.Debug("Found pivot block in block cache");
                return finalizedBlock.Header.Number;
            }
            if (_logger.IsDebug) _logger.Debug($"Hash of header found in block cache is {finalizedBlock.Header.Hash} when expecting {finalizedBlockHash}");
        }

        return null;
    }

    private long? TryGetFinalizedBlockNumberFromBlockTree(Keccak finalizedBlockHash)
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

    private async Task<long?> TryGetFinalizedBlockNumberFromPeers(Keccak finalizedBlockHash, CancellationToken cancellationToken)
    {
        foreach (PeerInfo peer in _syncPeerPool.InitializedPeers)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            try
            {
                if (_logger.IsInfo) _logger.Info($"Asking peer {peer.SyncPeer.Node.ClientId} for header of pivot block {finalizedBlockHash}");
                BlockHeader? finalizedHeader = await peer.SyncPeer.GetHeadBlockHeader(finalizedBlockHash, cancellationToken);
                if (finalizedHeader is not null)
                {
                    if (HeaderValidator.ValidateHash(finalizedHeader))
                    {
                        if (_logger.IsInfo) _logger.Info($"Received header of pivot block from peer {peer.SyncPeer.Node.ClientId}");
                        return finalizedHeader.Number;
                    }
                    if (_logger.IsInfo) _logger.Info($"Hash of header received from peer {peer.SyncPeer.Node.ClientId} is {finalizedHeader.Hash} when expecting {finalizedBlockHash}");
                }
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                if (_logger.IsInfo) _logger.Info($"Peer {peer.SyncPeer.Node.ClientId} didn't respond to request for header of pivot block {finalizedBlockHash}");
                if (_logger.IsDebug) _logger.Debug($"Exception in GetHeadBlockHeader request to peer {peer.SyncPeer.Node.ClientId}. {exception}");
            }
        }

        if (_logger.IsInfo && (_maxAttempts - _attemptsLeft) % 10 == 0) _logger.Info($"Potential new pivot block hash: {finalizedBlockHash}. Waiting for pivot block header [{_maxAttempts - _attemptsLeft}s]");
        return null;
    }

    private bool TryOverwritePivot(Keccak finalizedBlockHash, long finalizedBlockNumber)
    {
        long targetBlock = _beaconSyncStrategy.GetTargetBlockHeight() ?? 0;
        bool isCloseToHead = targetBlock <= finalizedBlockNumber || (targetBlock - finalizedBlockNumber) < Constants.MaxDistanceFromHead;
        bool newPivotHigherThanOld = finalizedBlockNumber > _syncConfig.PivotNumberParsed;

        if (isCloseToHead && newPivotHigherThanOld)
        {
            _syncConfig.PivotHash = finalizedBlockHash.ToString();
            _syncConfig.PivotNumber = finalizedBlockNumber.ToString();
            _syncConfig.MaxAttemptsToUpdatePivot = 0;

            RlpStream pivotData = new(38); //1 byte (prefix) + 4 bytes (long) + 1 byte (prefix) + 32 bytes (Keccak)
            pivotData.Encode(finalizedBlockNumber);
            pivotData.Encode(finalizedBlockHash);
            _metadataDb.Set(MetadataDbKeys.UpdatedPivotData, pivotData.Data!);

            if (_logger.IsInfo) _logger.Info($"New pivot block has been set based on ForkChoiceUpdate from CL. Pivot block number: {finalizedBlockNumber}, hash: {finalizedBlockHash}");
            return true;
        }

        if (!isCloseToHead && _logger.IsInfo) _logger.Info($"Pivot block from Consensus Layer too far from head. PivotBlockNumber: {finalizedBlockNumber}, TargetBlockNumber: {targetBlock}, difference: {targetBlock - finalizedBlockNumber} blocks. Max difference allowed: {Constants.MaxDistanceFromHead}");
        if (!newPivotHigherThanOld && _logger.IsInfo) _logger.Info($"Pivot block from Consensus Layer isn't higher than pivot from initial config. New PivotBlockNumber: {finalizedBlockNumber}, old: {_syncConfig.PivotNumber}");
        return false;
    }
}
