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

public sealed class EthCapabilitiesProvider(
    IReadOnlyBlockTree blockTree,
    IStateBoundary stateBoundary,
    ISyncConfig syncConfig,
    ISyncPointers syncPointers,
    IHistoryConfig historyConfig,
    IHistoryPruner historyPruner) : IEthCapabilitiesProvider
{
    private static readonly ResourceAvailability Disabled = new(true);

    public EthCapabilities GetCapabilities()
    {
        BlockHeader? head = blockTree.Head?.Header;
        if (head is null)
        {
            return new EthCapabilities(new ChainHead(0, Keccak.Zero), Disabled, Disabled, Disabled, Disabled);
        }

        bool fastSyncing = syncConfig.FastSync;
        long pivot = syncConfig.PivotNumber;
        long bodiesBarrier = pivot == 0 ? 0 : Math.Min(pivot, syncConfig.AncientBodiesBarrier);
        long receiptsBarrier = pivot == 0 ? 0 : Math.Min(pivot, Math.Max(syncConfig.AncientBodiesBarrier, syncConfig.AncientReceiptsBarrier));

        // Barriers are the eventual floor; before the descending pointer is set we have nothing.
        bool bodiesDownloaded = IsDescendingResourceDownloaded(fastSyncing, pivot, syncConfig.DownloadBodiesInFastSync, syncPointers.LowestInsertedBodyNumber);
        bool receiptsDownloaded = bodiesDownloaded && IsDescendingResourceDownloaded(fastSyncing, pivot, syncConfig.DownloadReceiptsInFastSync, syncPointers.LowestInsertedReceiptBlockNumber);

        long historyFloor = historyPruner.OldestBlockHeader?.Number ?? 0;
        DeleteStrategy? historyWindow = BuildWindow(
            historyConfig.Pruning == PruningModes.Rolling ? historyPruner.GetRetentionBlocks(historyConfig.RetentionEpochs) : 0);

        long lowestReceipt = Math.Max(syncPointers.LowestInsertedReceiptBlockNumber ?? 0L, receiptsBarrier);
        long lowestBody = Math.Max(syncPointers.LowestInsertedBodyNumber ?? 0L, bodiesBarrier);
        long lowestBlock = Math.Max(blockTree.LowestInsertedHeader?.Number ?? 0L, lowestBody);

        ResourceAvailability state = BuildState(head, fastSyncing);

        return new EthCapabilities(
            Head: new ChainHead(head.Number, head.Hash!),
            State: state,
            Receipts: BuildResource(receiptsDownloaded, Math.Max(lowestReceipt, historyFloor), historyWindow),
            Blocks: BuildResource(blockTree.BestSuggestedHeader is not null && bodiesDownloaded, Math.Max(lowestBlock, historyFloor), historyWindow),
            Stateproofs: state);
    }

    private ResourceAvailability BuildState(BlockHeader head, bool fastSyncing)
    {
        long? oldestStateBlock = stateBoundary.OldestStateBlock;
        // During fast sync, state isn't queryable until StateSyncRunner writes the pivot floor.
        if (fastSyncing && oldestStateBlock is null) return Disabled;

        long stateFloor = oldestStateBlock ?? 0L;
        if (stateBoundary.RetentionWindowBlocks is not { } retention)
            return new ResourceAvailability(Disabled: false, OldestBlock: stateFloor, DeleteStrategy: null);

        long windowOldest = Math.Max(0L, head.Number - retention);
        long stateOldest = Math.Max(stateFloor, windowOldest);
        // Emit the window only when it's the binding constraint, and report the configured
        // retention so the value stays accurate before head reaches it.
        DeleteStrategy? window = windowOldest >= stateFloor ? BuildWindow(retention) : null;
        return new ResourceAvailability(Disabled: false, OldestBlock: stateOldest, DeleteStrategy: window);
    }

    private static bool IsDescendingResourceDownloaded(bool fastSyncing, long pivot, bool downloadInFastSync, long? pointer) =>
        !fastSyncing || (downloadInFastSync && (pivot == 0 || pointer is not null));

    private static ResourceAvailability BuildResource(bool enabled, long oldestBlock, DeleteStrategy? deleteStrategy) =>
        enabled ? new ResourceAvailability(false, oldestBlock, deleteStrategy) : Disabled;

    private static DeleteStrategy? BuildWindow(long retentionBlocks) =>
        retentionBlocks > 0 ? new DeleteStrategy("window", retentionBlocks) : null;
}
