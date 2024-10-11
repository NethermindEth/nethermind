// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Optimism;

public class OptimismPivotUpdator(
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
    protected override Hash256? GetPotentialPivotBlockHash()
    {
        // getting potentially unsafe head block hash, because optimism isn't providing finalized one until fully synced
        return _beaconSyncStrategy.GetHeadBlockHash();
    }
}
