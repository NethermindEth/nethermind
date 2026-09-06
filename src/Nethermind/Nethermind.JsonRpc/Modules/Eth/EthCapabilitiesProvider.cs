// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
        ulong pivot = syncConfig.PivotNumber;
        ulong bodiesBarrier = pivot == 0ul ? 0ul : Math.Min(pivot, syncConfig.AncientBodiesBarrier);
        ulong receiptsBarrier = pivot == 0ul ? 0ul : Math.Min(pivot, Math.Max(syncConfig.AncientBodiesBarrier, syncConfig.AncientReceiptsBarrier));

        // Barriers are the eventual floor; before the descending pointer is set we have nothing.
        bool bodiesDownloaded = IsDescendingResourceDownloaded(fastSyncing, pivot, syncConfig.DownloadBodiesInFastSync, syncPointers.LowestInsertedBodyNumber);
        bool receiptsDownloaded = bodiesDownloaded && IsDescendingResourceDownloaded(fastSyncing, pivot, syncConfig.DownloadReceiptsInFastSync, syncPointers.LowestInsertedReceiptBlockNumber);

        ulong historyFloor = historyPruner.OldestBlockHeader?.Number ?? 0ul;
        DeleteStrategy? historyWindow = BuildWindow(
            historyConfig.Pruning == PruningModes.Rolling ? historyPruner.GetRetentionBlocks(historyConfig.RetentionEpochs) : 0UL);

        ulong lowestReceipt = Math.Max(syncPointers.LowestInsertedReceiptBlockNumber ?? 0ul, receiptsBarrier);
        ulong lowestBody = Math.Max(syncPointers.LowestInsertedBodyNumber ?? 0ul, bodiesBarrier);
        ulong lowestBlock = Math.Max(blockTree.LowestInsertedHeader?.Number ?? 0ul, lowestBody);

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
        ulong? oldestStateBlock = stateBoundary.OldestStateBlock;
        // During fast sync, state isn't queryable until StateSyncRunner writes the pivot floor.
        if (fastSyncing && oldestStateBlock is null) return Disabled;

        ulong stateFloor = oldestStateBlock ?? 0UL;
        if (stateBoundary.RetentionWindowBlocks is not { } retention)
            return new ResourceAvailability(Disabled: false, OldestBlock: stateFloor, DeleteStrategy: null);

        ulong windowOldest = head.Number.SaturatingSub(retention);
        ulong stateOldest = Math.Max(stateFloor, windowOldest);
        // Emit the window only when it's the binding constraint, and report the configured
        // retention so the value stays accurate before head reaches it.
        DeleteStrategy? window = windowOldest >= stateFloor ? BuildWindow(retention) : null;
        return new ResourceAvailability(Disabled: false, OldestBlock: stateOldest, DeleteStrategy: window);
    }

    private static bool IsDescendingResourceDownloaded(bool fastSyncing, ulong pivot, bool downloadInFastSync, ulong? pointer) =>
        !fastSyncing || (downloadInFastSync && (pivot == 0ul || pointer is not null));

    private static ResourceAvailability BuildResource(bool enabled, ulong oldestBlock, DeleteStrategy? deleteStrategy) =>
        enabled ? new ResourceAvailability(false, oldestBlock, deleteStrategy) : Disabled;

    private static DeleteStrategy? BuildWindow(ulong retentionBlocks) =>
        retentionBlocks > 0 ? new DeleteStrategy("window", retentionBlocks) : null;
}
