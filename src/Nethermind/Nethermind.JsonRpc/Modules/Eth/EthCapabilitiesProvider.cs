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
    IWorldStateManager worldStateManager,
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

        bool bodiesSynced = !fastSyncing || syncConfig.DownloadBodiesInFastSync;
        bool receiptsSynced = bodiesSynced && (!fastSyncing || syncConfig.DownloadReceiptsInFastSync);

        long historyFloor = historyPruner.OldestBlockHeader?.Number ?? 0;
        DeleteStrategy? historyWindow = BuildWindow(
            historyConfig.Pruning == PruningModes.Rolling ? historyPruner.GetRetentionBlocks(historyConfig.RetentionEpochs) : 0);

        long lowestReceipt = Math.Max(syncPointers.LowestInsertedReceiptBlockNumber ?? 0L, receiptsBarrier);
        long lowestBody = Math.Max(syncPointers.LowestInsertedBodyNumber ?? 0L, bodiesBarrier);
        long lowestBlock = Math.Max(blockTree.LowestInsertedHeader?.Number ?? 0L, lowestBody);

        ResourceAvailability state = BuildState(head, fastSyncing);

        // Stateproofs alias state for both Nethermind storage backends: trie storage stores trie
        // nodes by hash, flat storage retains them in path-keyed columns (see BaseTriePersistence).
        // Both serve eth_getProof in O(trie-depth) for any block where state is available.
        return new EthCapabilities(
            Head: new ChainHead(head.Number, head.Hash!),
            State: state,
            Receipts: BuildResource(receiptsSynced, Math.Max(lowestReceipt, historyFloor), historyWindow),
            Blocks: BuildResource(blockTree.BestSuggestedHeader is not null && bodiesSynced, Math.Max(lowestBlock, historyFloor), historyWindow),
            Stateproofs: state);
    }

    private ResourceAvailability BuildState(BlockHeader head, bool fastSyncing)
    {
        // During fast sync, state isn't queryable until StateSyncRunner finalises and writes the
        // pivot floor — disable the resource until then so callers don't see "available from genesis".
        if (fastSyncing && worldStateManager.OldestStateBlock is null) return Disabled;

        long stateFloor = worldStateManager.OldestStateBlock ?? 0L;
        long? windowOldest = worldStateManager.GetOldestStateBlock(head.Number);
        long stateOldest = Math.Max(stateFloor, windowOldest ?? 0L);
        // Only emit the window descriptor when the rolling window is the binding constraint;
        // when the static floor dominates, advertising "window:N" would mislead routers.
        DeleteStrategy? window = windowOldest is { } w && w >= stateFloor ? BuildWindow(head.Number - w) : null;
        return new ResourceAvailability(Disabled: false, OldestBlock: stateOldest, DeleteStrategy: window);
    }

    private static ResourceAvailability BuildResource(bool enabled, long oldestBlock, DeleteStrategy? deleteStrategy) =>
        enabled ? new ResourceAvailability(false, oldestBlock, deleteStrategy) : Disabled;

    private static DeleteStrategy? BuildWindow(long retentionBlocks) =>
        retentionBlocks > 0 ? new DeleteStrategy("window", retentionBlocks) : null;
}
