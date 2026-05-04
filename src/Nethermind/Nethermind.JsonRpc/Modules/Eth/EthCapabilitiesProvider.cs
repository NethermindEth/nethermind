// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.History;
using Nethermind.State;

namespace Nethermind.JsonRpc.Modules.Eth;

public class EthCapabilitiesProvider(
    IBlockTree blockTree,
    IWorldStateManager worldStateManager,
    ISyncConfig? syncConfig = null,
    IHistoryConfig? historyConfig = null,
    IHistoryPruner? historyPruner = null) : IEthCapabilitiesProvider
{
    public EthCapabilities GetCapabilities()
    {
        BlockHeader? head = blockTree.Head?.Header;
        bool headersAvailable = blockTree.BestSuggestedHeader is not null;
        long lowestBlock = blockTree.LowestInsertedHeader?.Number ?? 0;

        bool receiptsSynced = syncConfig?.DownloadReceiptsInFastSync ?? true;
        // AncientReceiptsBarrierCalc returns Math.Max(1, …) which is wrong for archive nodes
        // (PivotNumber = 0). When there was no fast sync, receipts are available from genesis.
        long oldestReceipts = (syncConfig?.PivotNumber ?? 0) == 0 ? 0 : syncConfig!.AncientReceiptsBarrierCalc;

        // State availability comes from IWorldStateManager so trie-pruning and flat-DB
        // implementations report their own retention semantics.
        StateAvailability stateAvailability = worldStateManager.StateAvailability;
        long? stateOldest = stateAvailability.Archive ? 0L
            : stateAvailability.RetentionWindowBlocks is { } window && head is not null
                ? Math.Max(0L, head.Number - window)
                : null;

        // History pruning (EIP-4444) deletes old blocks/receipts. The post-pruning floor is
        // historyPruner.OldestBlockHeader.Number; Rolling mode produces a known retention window.
        long historyFloor = historyPruner?.OldestBlockHeader?.Number ?? 0;
        DeleteStrategy? historyWindow = historyConfig?.Pruning == PruningModes.Rolling
            ? new DeleteStrategy("window", historyConfig.RetentionEpochs * HistoryPruner.SlotsPerEpoch)
            : null;

        return new EthCapabilities(
            Head: new ChainHead(head?.Number ?? 0, head?.Hash ?? Keccak.Zero),
            // State is always available (at minimum the genesis trie exists), even on a memory-pruned
            // node that hasn't yet synced past the first block — in which case OldestBlock is null
            // because the rolling window lower bound cannot yet be computed.
            State: Resource(
                enabled: true,
                oldestBlock: stateOldest,
                deleteStrategy: stateAvailability.RetentionWindowBlocks > 0
                    ? new DeleteStrategy("window", stateAvailability.RetentionWindowBlocks.Value)
                    : null),
            // Receipts/Tx/Logs share storage and pruning policy in Nethermind — one descriptor covers
            // all three. EthCapabilities.Tx and .Logs are computed properties that alias .Receipts.
            Receipts: Resource(receiptsSynced, Math.Max(oldestReceipts, historyFloor), historyWindow),
            Blocks: Resource(headersAvailable, Math.Max(lowestBlock, historyFloor), historyWindow),
            Stateproofs: Resource(stateAvailability.StateProofsSupported, 0L));
    }

    /// <summary>
    /// Builds a single resource descriptor. When <paramref name="enabled"/> is false, the spec
    /// requires <c>disabled: true</c> with no other fields present — the helper enforces that
    /// by zeroing <c>OldestBlock</c> and <c>DeleteStrategy</c> regardless of what was passed.
    /// </summary>
    private static ResourceAvailability Resource(
        bool enabled,
        long? oldestBlock,
        DeleteStrategy? deleteStrategy = null) =>
        new(Disabled: !enabled,
            OldestBlock: enabled ? oldestBlock : null,
            DeleteStrategy: enabled ? deleteStrategy : null);
}
