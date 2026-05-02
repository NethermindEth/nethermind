// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.History;

namespace Nethermind.JsonRpc.Modules.Eth;

public class EthCapabilitiesProvider(
    IBlockTree blockTree,
    ISyncConfig? syncConfig = null,
    IPruningConfig? pruningConfig = null,
    IHistoryConfig? historyConfig = null,
    IHistoryPruner? historyPruner = null) : IEthCapabilitiesProvider
{
    private const int SlotsPerEpoch = 32;

    public EthCapabilities GetCapabilities()
    {
        BlockHeader? head = blockTree.Head?.Header;
        bool headersAvailable = blockTree.BestSuggestedHeader is not null;
        long lowestBlock = blockTree.LowestInsertedHeader?.Number ?? 0;

        bool receiptsSynced = syncConfig?.DownloadReceiptsInFastSync ?? true;
        // AncientReceiptsBarrierCalc returns Math.Max(1, …) which is wrong for archive nodes
        // (PivotNumber = 0). When there was no fast sync, receipts are available from genesis.
        long oldestReceipts = (syncConfig?.PivotNumber ?? 0) == 0 ? 0 : syncConfig!.AncientReceiptsBarrierCalc;

        PruningMode statePruningMode = pruningConfig?.Mode ?? PruningMode.None;
        bool isArchive = statePruningMode == PruningMode.None;
        // Memory (and Hybrid) pruning maintains a rolling window of PruningBoundary recent states.
        // Full-only pruning is periodic and non-linear — we cannot claim a predictable oldest block.
        long? stateRetention = statePruningMode.IsMemory() ? pruningConfig!.PruningBoundary : null;
        long? stateOldest = isArchive ? 0L
            : stateRetention is not null && head is not null ? Math.Max(0L, head.Number - stateRetention.Value)
            : null;

        // History pruning (EIP-4444) deletes old blocks/receipts. The post-pruning floor is
        // historyPruner.OldestBlockHeader.Number; Rolling mode produces a known retention window.
        long historyFloor = historyPruner?.OldestBlockHeader?.Number ?? 0;
        DeleteStrategy? historyWindow = historyConfig?.Pruning == PruningModes.Rolling
            ? new DeleteStrategy("window", historyConfig.RetentionEpochs * SlotsPerEpoch)
            : null;

        return new EthCapabilities(
            Head: new ChainHead(head?.Number ?? 0, head?.Hash ?? Keccak.Zero),
            // State is always available (at minimum the genesis trie exists), even on a memory-pruned
            // node that hasn't yet synced past the first block — in which case OldestBlock is null
            // because the rolling window lower bound cannot yet be computed.
            State: new ResourceAvailability(
                Disabled: false,
                OldestBlock: stateOldest,
                DeleteStrategy: stateRetention > 0 ? new DeleteStrategy("window", stateRetention.Value) : null),
            // Receipts/Tx/Logs share storage and pruning policy in Nethermind — one descriptor covers
            // all three. EthCapabilities.Tx and .Logs are computed properties that alias .Receipts.
            Receipts: new ResourceAvailability(
                Disabled: !receiptsSynced,
                OldestBlock: receiptsSynced ? Math.Max(oldestReceipts, historyFloor) : null,
                DeleteStrategy: receiptsSynced ? historyWindow : null),
            Blocks: new ResourceAvailability(
                Disabled: !headersAvailable,
                OldestBlock: headersAvailable ? Math.Max(lowestBlock, historyFloor) : null,
                DeleteStrategy: headersAvailable ? historyWindow : null),
            Stateproofs: new ResourceAvailability(Disabled: !isArchive, OldestBlock: isArchive ? 0L : null));
    }
}
