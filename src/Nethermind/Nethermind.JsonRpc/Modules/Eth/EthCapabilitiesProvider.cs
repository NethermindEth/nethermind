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

        // Floor recorded by sync completion / full-pruning runs; defaults to genesis.
        long stateFloor = blockTree.OldestStateBlock ?? 0L;
        long? windowOldest = head is not null ? worldStateManager.GetOldestStateBlock(head.Number) : null;
        long stateOldest = Math.Max(stateFloor, windowOldest ?? 0L);
        // Report a window descriptor only when retention is rolling (windowOldest non-null and head is set).
        long? retentionDepth = head is not null && windowOldest is { } w ? head.Number - w : null;
        DeleteStrategy? stateWindow = retentionDepth > 0 ? new DeleteStrategy("window", retentionDepth.Value) : null;

        long historyFloor = historyPruner?.OldestBlockHeader?.Number ?? 0;
        DeleteStrategy? historyWindow = historyConfig?.Pruning == PruningModes.Rolling
            ? new DeleteStrategy("window", historyConfig.RetentionEpochs * HistoryPruner.SlotsPerEpoch)
            : null;

        ResourceAvailability state = Resource(enabled: true, stateOldest, stateWindow);
        return new EthCapabilities(
            Head: new ChainHead(head?.Number ?? 0, head?.Hash ?? Keccak.Zero),
            State: state,
            // Tx and Logs are computed aliases of Receipts.
            Receipts: Resource(receiptsSynced, Math.Max(oldestReceipts, historyFloor), historyWindow),
            Blocks: Resource(headersAvailable, Math.Max(lowestBlock, historyFloor), historyWindow),
            Stateproofs: state);
    }

    private static ResourceAvailability Resource(bool enabled, long? oldestBlock, DeleteStrategy? deleteStrategy = null) =>
        new(Disabled: !enabled,
            OldestBlock: enabled ? oldestBlock : null,
            DeleteStrategy: enabled ? deleteStrategy : null);
}
