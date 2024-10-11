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
    protected readonly IBeaconSyncStrategy _beaconSyncStrategy;
    private readonly IDb _metadataDb;
    private readonly ILogger _logger;

    private readonly CancellationTokenSource _cancellation = new();

    private static int _maxAttempts;
    private int _attemptsLeft;
    private int _updateInProgress;
    private Hash256 _alreadyAnnouncedNewPivotHash = Keccak.Zero;

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
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

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
                Hash256 updatedPivotBlockHash = pivotStream.DecodeKeccak()!;

                if (updatedPivotBlockHash.IsZero)
                {
                    return false;
                }
                UpdateConfigValues(updatedPivotBlockHash, updatedPivotBlockNumber);

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
        Hash256? potentialPivotBlockHash = TryGetPotentialPivotBlockHashFromCl();

        if (potentialPivotBlockHash is null || potentialPivotBlockHash == Keccak.Zero)
        {
            return false;
        }

        long? potentialPivotBlockNumber = TryGetPotentialPivotBlockNumberFromBlockCache(potentialPivotBlockHash);
        potentialPivotBlockNumber ??= TryGetPotentialPivotBlockNumberFromBlockTree(potentialPivotBlockHash);
        potentialPivotBlockNumber ??= await TryGetPotentialPivotBlockNumberFromPeers(potentialPivotBlockHash, cancellationToken);

        return potentialPivotBlockNumber is not null && TryOverwritePivot(potentialPivotBlockHash, (long)potentialPivotBlockNumber);
    }

    private Hash256? TryGetPotentialPivotBlockHashFromCl()
    {
        Hash256? potentialPivotBlockHash = GetPotentialPivotBlockHash();

        if (potentialPivotBlockHash is null || potentialPivotBlockHash == Keccak.Zero)
        {
            if (_logger.IsInfo && (_maxAttempts - _attemptsLeft) % 10 == 0) _logger.Info($"Waiting for Forkchoice message from Consensus Layer to set fresh pivot block [{_maxAttempts - _attemptsLeft}s]");

            return null;
        }

        if (_alreadyAnnouncedNewPivotHash != potentialPivotBlockHash)
        {
            if (_logger.IsInfo) _logger.Info($"Potential new pivot block hash: {potentialPivotBlockHash}");
            _alreadyAnnouncedNewPivotHash = potentialPivotBlockHash;
        }

        return potentialPivotBlockHash;
    }

    protected virtual Hash256? GetPotentialPivotBlockHash()
    {
        // getting finalized block hash as it is safe, because can't be reorganized
        return _beaconSyncStrategy.GetFinalizedHash();
    }

    private long? TryGetPotentialPivotBlockNumberFromBlockCache(Hash256 potentialPivotBlockHash)
    {
        if (_logger.IsDebug) _logger.Debug("Looking for pivot block in block cache");
        if (_blockCacheService.BlockCache.TryGetValue(potentialPivotBlockHash, out Block? potentialPivotBlock))
        {
            if (HeaderValidator.ValidateHash(potentialPivotBlock.Header))
            {
                if (_logger.IsDebug) _logger.Debug("Found pivot block in block cache");
                return potentialPivotBlock.Header.Number;
            }
            if (_logger.IsDebug) _logger.Debug($"Hash of header found in block cache is {potentialPivotBlock.Header.Hash} when expecting {potentialPivotBlockHash}");
        }

        return null;
    }

    private long? TryGetPotentialPivotBlockNumberFromBlockTree(Hash256 potentialPivotBlockHash)
    {
        if (_logger.IsDebug) _logger.Debug("Looking for header of pivot block in blockTree");
        BlockHeader? potentialPivotBlock = _blockTree.FindHeader(potentialPivotBlockHash, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
        if (potentialPivotBlock is not null)
        {
            if (HeaderValidator.ValidateHash(potentialPivotBlock))
            {
                if (_logger.IsDebug) _logger.Debug("Found header of pivot block in block tree");
                return potentialPivotBlock.Number;
            }
            if (_logger.IsDebug) _logger.Debug($"Hash of header found in block tree is {potentialPivotBlock.Hash} when expecting {potentialPivotBlockHash}");
        }

        return null;
    }

    private async Task<long?> TryGetPotentialPivotBlockNumberFromPeers(Hash256 potentialPivotBlockHash, CancellationToken cancellationToken)
    {
        foreach (PeerInfo peer in _syncPeerPool.InitializedPeers)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            try
            {
                if (_logger.IsInfo) _logger.Info($"Asking peer {peer.SyncPeer.Node.ClientId} for header of pivot block {potentialPivotBlockHash}");
                BlockHeader? potentialPivotBlock = await peer.SyncPeer.GetHeadBlockHeader(potentialPivotBlockHash, cancellationToken);
                if (potentialPivotBlock is not null)
                {
                    if (HeaderValidator.ValidateHash(potentialPivotBlock))
                    {
                        if (_logger.IsInfo) _logger.Info($"Received header of pivot block from peer {peer.SyncPeer.Node.ClientId}");
                        return potentialPivotBlock.Number;
                    }
                    if (_logger.IsInfo) _logger.Info($"Hash of header received from peer {peer.SyncPeer.Node.ClientId} is {potentialPivotBlock.Hash} when expecting {potentialPivotBlockHash}");
                }
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                if (_logger.IsInfo) _logger.Info($"Peer {peer.SyncPeer.Node.ClientId} didn't respond to request for header of pivot block {potentialPivotBlockHash}");
                if (_logger.IsDebug) _logger.Debug($"Exception in GetHeadBlockHeader request to peer {peer.SyncPeer.Node.ClientId}. {exception}");
            }
        }

        if (_logger.IsInfo && (_maxAttempts - _attemptsLeft) % 10 == 0) _logger.Info($"Potential new pivot block hash: {potentialPivotBlockHash}. Waiting for pivot block header [{_maxAttempts - _attemptsLeft}s]");
        return null;
    }

    private bool TryOverwritePivot(Hash256 potentialPivotBlockHash, long potentialPivotBlockNumber)
    {
        long targetBlock = _beaconSyncStrategy.GetTargetBlockHeight() ?? 0;
        bool isCloseToHead = targetBlock <= potentialPivotBlockNumber || (targetBlock - potentialPivotBlockNumber) < Constants.MaxDistanceFromHead;
        bool newPivotHigherThanOld = potentialPivotBlockNumber > _syncConfig.PivotNumberParsed;

        if (isCloseToHead && newPivotHigherThanOld)
        {
            UpdateConfigValues(potentialPivotBlockHash, potentialPivotBlockNumber);

            RlpStream pivotData = new(38); //1 byte (prefix) + 4 bytes (long) + 1 byte (prefix) + 32 bytes (Keccak)
            pivotData.Encode(potentialPivotBlockNumber);
            pivotData.Encode(potentialPivotBlockHash);
            _metadataDb.Set(MetadataDbKeys.UpdatedPivotData, pivotData.Data.ToArray()!);

            if (_logger.IsInfo) _logger.Info($"New pivot block has been set based on ForkChoiceUpdate from CL. Pivot block number: {potentialPivotBlockNumber}, hash: {potentialPivotBlockHash}");
            return true;
        }

        if (!isCloseToHead && _logger.IsInfo) _logger.Info($"Pivot block from Consensus Layer too far from head. PivotBlockNumber: {potentialPivotBlockNumber}, TargetBlockNumber: {targetBlock}, difference: {targetBlock - potentialPivotBlockNumber} blocks. Max difference allowed: {Constants.MaxDistanceFromHead}");
        if (!newPivotHigherThanOld && _logger.IsInfo) _logger.Info($"Pivot block from Consensus Layer isn't higher than pivot from initial config. New PivotBlockNumber: {potentialPivotBlockNumber}, old: {_syncConfig.PivotNumber}");
        return false;
    }

    private void UpdateConfigValues(Hash256 finalizedBlockHash, long finalizedBlockNumber)
    {
        _syncConfig.PivotHash = finalizedBlockHash.ToString();
        _syncConfig.PivotNumber = finalizedBlockNumber.ToString();
        _syncConfig.MaxAttemptsToUpdatePivot = 0;
    }

}
