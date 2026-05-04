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
        // AncientReceiptsBarrierCalc returns Math.Max(1, …) which would be wrong for PivotNumber=0.
        long oldestReceipts = (syncConfig?.PivotNumber ?? 0) == 0 ? 0 : syncConfig!.AncientReceiptsBarrierCalc;

        StateAvailability stateAvailability = worldStateManager.StateAvailability;
        long? stateOldest = stateAvailability.Archive ? 0L
            : stateAvailability.RetentionWindowBlocks is { } window && head is not null
                ? Math.Max(0L, head.Number - window)
                : null;
        DeleteStrategy? stateWindow = stateAvailability.RetentionWindowBlocks > 0
            ? new DeleteStrategy("window", stateAvailability.RetentionWindowBlocks.Value)
            : null;

        long historyFloor = historyPruner?.OldestBlockHeader?.Number ?? 0;
        DeleteStrategy? historyWindow = historyConfig?.Pruning == PruningModes.Rolling
            ? new DeleteStrategy("window", historyConfig.RetentionEpochs * HistoryPruner.SlotsPerEpoch)
            : null;

        return new EthCapabilities(
            Head: new ChainHead(head?.Number ?? 0, head?.Hash ?? Keccak.Zero),
            State: Resource(enabled: true, stateOldest, stateWindow),
            // Tx and Logs are computed aliases of Receipts.
            Receipts: Resource(receiptsSynced, Math.Max(oldestReceipts, historyFloor), historyWindow),
            Blocks: Resource(headersAvailable, Math.Max(lowestBlock, historyFloor), historyWindow),
            // Proofs share state's retention; flat storage has no by-hash lookup so proofs are off.
            Stateproofs: Resource(stateAvailability.StateProofsSupported, stateOldest, stateWindow));
    }

    private static ResourceAvailability Resource(bool enabled, long? oldestBlock, DeleteStrategy? deleteStrategy = null) =>
        new(Disabled: !enabled,
            OldestBlock: enabled ? oldestBlock : null,
            DeleteStrategy: enabled ? deleteStrategy : null);
}
