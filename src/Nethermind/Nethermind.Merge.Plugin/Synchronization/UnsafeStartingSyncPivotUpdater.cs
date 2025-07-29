// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;

using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Merge.Plugin.Synchronization;

public class UnsafeStartingSyncPivotUpdater(
    IBlockTree blockTree,
    ISyncModeSelector syncModeSelector,
    ISyncPeerPool syncPeerPool,
    ISyncConfig syncConfig,
    IBlockCacheService blockCacheService,
    IBeaconSyncStrategy beaconSyncStrategy,
    ILogManager logManager)
    : StartingSyncPivotUpdater(blockTree, syncModeSelector, syncPeerPool, syncConfig,
        blockCacheService, beaconSyncStrategy, logManager)
{
    protected override async Task<(Hash256 Hash, long Number)?> TryGetPivotData(CancellationToken cancellationToken)
    {
        // getting potentially unsafe head block hash, because some chains (e.g. optimism) aren't providing finalized block hash until fully synced
        Hash256? headBlockHash = _beaconSyncStrategy.GetHeadBlockHash();

        if (headBlockHash is not null && headBlockHash != Keccak.Zero)
        {
            const string head = "head";
            long? headBlockNumber = TryGetBlockNumberFromBlockCache(headBlockHash, head)
                                    ?? await TryGetFromPeers(headBlockHash, cancellationToken, head)
                                    ?? 0;

            if (headBlockNumber > Reorganization.MaxDepth)
            {
                long potentialPivotBlockNumber = headBlockNumber.Value - Reorganization.MaxDepth;

                Hash256? potentialPivotBlockHash =
                    TryGetPotentialPivotBlockNumberFromBlockCache(potentialPivotBlockNumber)
                    ?? (await TryGetFromPeers(potentialPivotBlockNumber, cancellationToken))?.Hash;

                if (potentialPivotBlockHash is not null && potentialPivotBlockHash != Keccak.Zero)
                {
                    UpdateAndPrintPotentialNewPivot(potentialPivotBlockHash);
                    return (potentialPivotBlockHash, potentialPivotBlockNumber);
                }
            }
        }

        return null;
    }

    private async Task<BlockHeader?> TryGetFromPeers(long blockNumber, CancellationToken cancellationToken) =>
        await TryGetFromPeers(blockNumber, cancellationToken, static async (peer, number, token) =>
        {
            using IOwnedReadOnlyList<BlockHeader>? x = await peer.GetBlockHeaders(number, 1, 0, token);
            return x?.Count == 1 ? x[0] : null;
        });

    private Hash256? TryGetPotentialPivotBlockNumberFromBlockCache(long potentialPivotBlockNumber)
    {
        if (_logger.IsDebug) _logger.Debug("Looking for header of pivot block in block cache");

        foreach (Block block in _blockCacheService.BlockCache.Values)
        {
            if (block.Number == potentialPivotBlockNumber && HeaderValidator.ValidateHash(block.Header))
            {
                if (_logger.IsInfo) _logger.Info($"Loaded potential pivot block {potentialPivotBlockNumber} from block cache. Hash: {block.Hash}");
                return block.Hash;
            }
        }

        if (_logger.IsDebug) _logger.Debug("Header of pivot block not found in block cache");
        return null;
    }
}
