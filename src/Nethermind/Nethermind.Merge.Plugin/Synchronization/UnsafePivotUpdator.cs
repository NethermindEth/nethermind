// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
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
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Merge.Plugin.Synchronization;

public class UnsafePivotUpdator(
    IBlockTree blockTree,
    ISyncModeSelector syncModeSelector,
    ISyncPeerPool syncPeerPool,
    ISyncConfig syncConfig,
    IBlockCacheService blockCacheService,
    IBeaconSyncStrategy beaconSyncStrategy,
    IDb metadataDb,
    ILogManager logManager)
    : PivotUpdator(blockTree, syncModeSelector, syncPeerPool, syncConfig,
        blockCacheService, beaconSyncStrategy, metadataDb, logManager)
{

    private const int NumberOfBlocksBehindHeadForSettingPivot = 64;

    protected override async Task<Hash256?> TryGetPotentialPivotBlockHash(CancellationToken cancellationToken)
    {
        // getting potentially unsafe head block hash, because some chains (e.g. optimism) aren't providing finalized block hash until fully synced
        Hash256? headBlockHash = _beaconSyncStrategy.GetHeadBlockHash();

        if (headBlockHash is not null
            && headBlockHash != Keccak.Zero)
        {
            long? headBlockNumber = TryGetHeadBlockNumberFromBlockCache(headBlockHash);
            headBlockNumber ??= await TryGetHeadBlockNumberFromPeers(headBlockHash, cancellationToken);
            if (headBlockNumber > NumberOfBlocksBehindHeadForSettingPivot)
            {
                long potentialPivotBlockNumber = (long)headBlockNumber - NumberOfBlocksBehindHeadForSettingPivot;

                Hash256? potentialPivotBlockHash = await TryGetPotentialPivotBlockHashFromPeers(potentialPivotBlockNumber, cancellationToken);

                if (potentialPivotBlockHash is not null
                    && potentialPivotBlockHash != Keccak.Zero)
                {
                    UpdateAndPrintPotentialNewPivot(potentialPivotBlockHash);
                    return potentialPivotBlockHash;
                }
            }
        }

        PrintWaitingForMessageFromCl();
        return null;
    }

    private long? TryGetHeadBlockNumberFromBlockCache(Hash256 headBlockHash)
    {
        if (_logger.IsDebug) _logger.Debug("Looking for head block in block cache");
        if (_blockCacheService.BlockCache.TryGetValue(headBlockHash, out Block? headBlock))
        {
            if (HeaderValidator.ValidateHash(headBlock.Header))
            {
                if (_logger.IsDebug) _logger.Debug("Found head block in block cache");
                return headBlock.Header.Number;
            }
            if (_logger.IsDebug) _logger.Debug($"Hash of header found in block cache is {headBlock.Header.Hash} when expecting {headBlockHash}");
        }

        return null;
    }

    private async Task<long> TryGetHeadBlockNumberFromPeers(Hash256 headBlockHash, CancellationToken cancellationToken)
    {
        foreach (PeerInfo peer in _syncPeerPool.InitializedPeers)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return 0;
            }
            try
            {
                if (_logger.IsInfo) _logger.Info($"Asking peer {peer.SyncPeer.Node.ClientId} for header of head block {headBlockHash}");
                BlockHeader? headBlockHeader = await peer.SyncPeer.GetHeadBlockHeader(headBlockHash, cancellationToken);
                if (headBlockHeader is not null)
                {
                    if (HeaderValidator.ValidateHash(headBlockHeader))
                    {
                        if (_logger.IsInfo) _logger.Info($"Received header of head block from peer {peer.SyncPeer.Node.ClientId}");
                        return headBlockHeader.Number;
                    }
                    if (_logger.IsInfo) _logger.Info($"Hash of header received from peer {peer.SyncPeer.Node.ClientId} is {headBlockHeader.Hash} when expecting {headBlockHash}");
                }
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                if (_logger.IsInfo) _logger.Info($"Peer {peer.SyncPeer.Node.ClientId} didn't respond to request for header of head block {headBlockHash}");
                if (_logger.IsDebug) _logger.Debug($"Exception in GetHeadBlockHeader request to peer {peer.SyncPeer.Node.ClientId}. {exception}");
            }
        }

        return 0;
    }

    private async Task<Hash256?> TryGetPotentialPivotBlockHashFromPeers(long potentialPivotBlockNumber, CancellationToken cancellationToken)
    {
        foreach (PeerInfo peer in _syncPeerPool.InitializedPeers)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            try
            {
                if (_logger.IsInfo) _logger.Info($"Asking peer {peer.SyncPeer.Node.ClientId} for header of pivot block {potentialPivotBlockNumber}");
                BlockHeader? potentialPivotBlockHeader = (await peer.SyncPeer.GetBlockHeaders(potentialPivotBlockNumber, 1, 0, cancellationToken))?[0];
                if (potentialPivotBlockHeader is not null)
                {
                    if (HeaderValidator.ValidateHash(potentialPivotBlockHeader))
                    {
                        if (_logger.IsInfo) _logger.Info($"Received header of pivot block from peer {peer.SyncPeer.Node.ClientId}");
                        return potentialPivotBlockHeader.Hash;
                    }
                    if (_logger.IsInfo) _logger.Info($"Header received from peer {peer.SyncPeer.Node.ClientId} wasn't valid");
                }
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                if (_logger.IsInfo) _logger.Info($"Peer {peer.SyncPeer.Node.ClientId} didn't respond to request for header of pivot block {potentialPivotBlockNumber}");
                if (_logger.IsDebug) _logger.Debug($"Exception in GetHeadBlockHeader request to peer {peer.SyncPeer.Node.ClientId}. {exception}");
            }
        }

        PrintPotentialNewPivotAndWaiting(potentialPivotBlockNumber.ToString());
        return null;
    }
}
