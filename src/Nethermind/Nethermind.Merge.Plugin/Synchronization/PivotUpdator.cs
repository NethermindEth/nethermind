// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
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
    private readonly IBeaconSyncStrategy _beaconSyncStrategy;
    private readonly ILogger _logger;

    private readonly CancellationTokenSource _cancellation = new();

    private int _attemptsLeft;
    private long _updateInProgress;
    private Keccak _alreadyAnnouncedNewPivotHash = Keccak.Zero;

    public PivotUpdator(IBlockTree blockTree,
        ISyncModeSelector syncModeSelector,
        ISyncPeerPool syncPeerPool,
        ISyncConfig syncConfig,
        IBeaconSyncStrategy beaconSyncStrategy,
        ILogManager logManager)
    {
        _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        _syncModeSelector = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
        _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
        _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
        _beaconSyncStrategy = beaconSyncStrategy ?? throw new ArgumentNullException(nameof(beaconSyncStrategy));
        _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

        _attemptsLeft = syncConfig.MaxAttemptsToUpdatePivot;
        _syncModeSelector.Changed += OnSyncModeChanged;
    }

    private async void OnSyncModeChanged(object? sender, SyncModeChangedEventArgs syncMode)
    {
        if ((syncMode.Current & SyncMode.UpdatingPivot) != 0 && Interlocked.Read(ref _updateInProgress) == 0)
        {
            Interlocked.Increment(ref _updateInProgress);
            if (await TrySetFreshPivot(_cancellation.Token))
            {
                _syncModeSelector.Changed -= OnSyncModeChanged;
            }
            else if (_attemptsLeft-- > 0)
            {
                Interlocked.Decrement(ref _updateInProgress);
            }
            else
            {
                _syncModeSelector.Changed -= OnSyncModeChanged;
                _syncConfig.MaxAttemptsToUpdatePivot = 0;
                if (_logger.IsWarn) _logger.Warn("Failed to update pivot block, skipping it.");
            }
        }
    }

    private async Task<bool> TrySetFreshPivot(CancellationToken cancellationToken)
    {
        Keccak? finalizedBlockHash = TryGetFinalizedBlockHashFromCl();

        if (finalizedBlockHash is null || finalizedBlockHash == Keccak.Zero)
        {
            return false;
        }

        long? finalizedBlockNumber = TryGetFinalizedBlockNumberFromBlockTree(finalizedBlockHash);
        finalizedBlockNumber ??= await TryGetFinalizedBlockNumberFromPeers(finalizedBlockHash, cancellationToken);

        return finalizedBlockNumber is not null && TryOverwritePivot(finalizedBlockHash, (long)finalizedBlockNumber);
    }

    private Keccak? TryGetFinalizedBlockHashFromCl()
    {
        Keccak? finalizedBlockHash = _beaconSyncStrategy.GetFinalizedHash();

        if (finalizedBlockHash is null || finalizedBlockHash == Keccak.Zero)
        {
            if (_logger.IsInfo) _logger.Info($"Waiting for Forkchoice message from Consensus Layer to set fresh pivot block. {_attemptsLeft} attempts left");

            return null;
        }

        if (_alreadyAnnouncedNewPivotHash != finalizedBlockHash)
        {
            if (_logger.IsInfo) _logger.Info($"New pivot block hash: {finalizedBlockHash}");
            _alreadyAnnouncedNewPivotHash = finalizedBlockHash;
        }

        return finalizedBlockHash;
    }

    private long? TryGetFinalizedBlockNumberFromBlockTree(Keccak finalizedBlockHash)
    {
        if (_logger.IsDebug) _logger.Debug("Looking for header of pivot block in blockTree");
        long? finalizedBlockNumber = _blockTree.FindHeader(finalizedBlockHash, BlockTreeLookupOptions.DoNotCreateLevelIfMissing)?.Number ?? null;
        if (finalizedBlockNumber is not null && _logger.IsInfo) _logger.Info("Found header of pivot block in blockTree");

        return finalizedBlockNumber;
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
                if (_logger.IsInfo) _logger.Info($"Asking peer {peer.SyncPeer.Node.ClientId} for header of pivot block");
                long finalizedBlockNumber = (await peer.SyncPeer.GetHeadBlockHeader(finalizedBlockHash, cancellationToken))?.Number ?? 0;
                if (finalizedBlockNumber != 0)
                {
                    if (_logger.IsInfo) _logger.Info($"Received header of pivot block from peer {peer.SyncPeer.Node.ClientId}");
                    return finalizedBlockNumber;
                }
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                if (_logger.IsInfo) _logger.Info($"Peer {peer.SyncPeer.Node.ClientId} didn't respond to request for header of pivot block {finalizedBlockHash}");
                if (_logger.IsDebug) _logger.Debug($"Exception in GetHeadBlockHeader request to peer {peer.SyncPeer.Node.ClientId}. {exception}");
            }
        }

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
            if (_logger.IsWarn) _logger.Warn($"New pivot block has been set based on FCU from CL. Pivot block number: {finalizedBlockNumber}, hash: {finalizedBlockHash}");
            return true;
        }

        if (_logger.IsInfo) _logger.Info($"Pivot block from Consensus Layer too far from head. PivotBlockNumber: {finalizedBlockNumber}, TargetBlockNumber: {targetBlock}, difference: {targetBlock - finalizedBlockNumber} blocks. Max difference allowed: {Constants.MaxDistanceFromHead}");
        return false;
    }
}
