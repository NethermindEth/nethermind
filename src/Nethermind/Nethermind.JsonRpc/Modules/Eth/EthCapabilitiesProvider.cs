// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.History;
using Nethermind.State;
using Nethermind.Synchronization;

namespace Nethermind.JsonRpc.Modules.Eth;

public class EthCapabilitiesProvider(
    IBlockTree blockTree,
    IWorldStateManager worldStateManager,
    ISyncConfig? syncConfig = null,
    ISyncPointers? syncPointers = null,
    IHistoryConfig? historyConfig = null,
    IHistoryPruner? historyPruner = null) : IEthCapabilitiesProvider
{
    public EthCapabilities GetCapabilities()
    {
        BlockHeader? head = blockTree.Head?.Header;
        if (head is null)
        {
            ResourceAvailability disabled = Resource(enabled: false, oldestBlock: null);
            return new EthCapabilities(new ChainHead(0, Keccak.Zero), disabled, disabled, disabled, disabled);
        }

        bool headersAvailable = blockTree.BestSuggestedHeader is not null;

        bool fastSyncing = syncConfig?.FastSync ?? false;
        long pivot = syncConfig?.PivotNumber ?? 0;
        long bodiesBarrier = pivot == 0 ? 0 : Math.Min(pivot, syncConfig!.AncientBodiesBarrier);
        long receiptsBarrier = pivot == 0 ? 0 : Math.Min(pivot, Math.Max(syncConfig!.AncientBodiesBarrier, syncConfig.AncientReceiptsBarrier));

        bool bodiesSynced = !fastSyncing || syncConfig!.DownloadBodiesInFastSync;
        bool receiptsSynced = bodiesSynced && (!fastSyncing || syncConfig!.DownloadReceiptsInFastSync);

        // Mid-sync: actual progress (LowestInserted*) tracks above the configured barrier and clamps
        // OldestBlock to what's currently available. Post-sync the pointer reaches the barrier and
        // the Math.Max collapses to the static floor.
        long lowestBody = Math.Max(syncPointers?.LowestInsertedBodyNumber ?? 0L, bodiesBarrier);
        long lowestReceipt = Math.Max(syncPointers?.LowestInsertedReceiptBlockNumber ?? 0L, receiptsBarrier);
        long lowestBlock = Math.Max(blockTree.LowestInsertedHeader?.Number ?? 0L, lowestBody);

        // During fast sync, state isn't queryable until StateSyncRunner finalises and writes
        // OldestStateBlock — disable State/Stateproofs in the gap so callers don't get told that
        // historical state exists when only the latest snapshot is being filled in.
        bool stateAvailable = !fastSyncing || blockTree.OldestStateBlock is not null;
        long stateFloor = blockTree.OldestStateBlock ?? 0L;
        long? windowOldest = worldStateManager.GetOldestStateBlock(head.Number);
        long stateOldest = Math.Max(stateFloor, windowOldest ?? 0L);
        // Only emit window descriptor when window is the binding constraint, not the static floor.
        DeleteStrategy? stateWindow = windowOldest is { } w && w >= stateFloor
            ? new DeleteStrategy("window", head.Number - w)
            : null;

        long historyFloor = historyPruner?.OldestBlockHeader?.Number ?? 0;
        DeleteStrategy? historyWindow = historyConfig?.Pruning == PruningModes.Rolling
            ? new DeleteStrategy("window", historyConfig.RetentionEpochs * HistoryPruner.SlotsPerEpoch)
            : null;

        ResourceAvailability state = Resource(stateAvailable, stateOldest, stateWindow);
        return new EthCapabilities(
            Head: new ChainHead(head.Number, head.Hash ?? Keccak.Zero),
            State: state,
            Receipts: Resource(receiptsSynced, Math.Max(lowestReceipt, historyFloor), historyWindow),
            Blocks: Resource(headersAvailable && bodiesSynced, Math.Max(lowestBlock, historyFloor), historyWindow),
            Stateproofs: state);
    }

    private static ResourceAvailability Resource(bool enabled, long? oldestBlock, DeleteStrategy? deleteStrategy = null) =>
        new(Disabled: !enabled,
            OldestBlock: enabled ? oldestBlock : null,
            DeleteStrategy: enabled ? deleteStrategy : null);
}
