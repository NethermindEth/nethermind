// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.JsonRpc.Modules.Eth;

public class EthCapabilitiesProvider(
    IBlockTree blockTree,
    ISyncConfig? syncConfig = null,
    IPruningConfig? pruningConfig = null) : IEthCapabilitiesProvider
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

        PruningMode mode = pruningConfig?.Mode ?? PruningMode.None;
        bool isArchive = mode == PruningMode.None;
        // Memory (and Hybrid) pruning maintains a rolling window of PruningBoundary recent states.
        // Full-only pruning is periodic and non-linear — we cannot claim a predictable oldest block.
        long? retentionBlocks = mode.IsMemory() ? pruningConfig!.PruningBoundary : null;
        long? stateOldest = isArchive ? 0L
            : retentionBlocks is not null && head is not null ? Math.Max(0L, head.Number - retentionBlocks.Value)
            : null;

        ResourceAvailability receipts = new(
            Disabled: !receiptsSynced,
            OldestBlock: receiptsSynced ? oldestReceipts : null);

        return new EthCapabilities(
            Head: new ChainHead(head?.Number ?? 0, head?.Hash ?? Keccak.Zero),
            // State is always available (at minimum the genesis trie exists), even on a memory-pruned
            // node that hasn't yet synced past the first block — in which case OldestBlock is null
            // because the rolling window lower bound cannot yet be computed.
            State: new ResourceAvailability(
                Disabled: false,
                OldestBlock: stateOldest,
                DeleteStrategy: retentionBlocks > 0 ? new DeleteStrategy("window", retentionBlocks.Value) : null),
            Tx: receipts,
            Logs: receipts,
            Receipts: receipts,
            Blocks: new ResourceAvailability(Disabled: !headersAvailable, OldestBlock: headersAvailable ? lowestBlock : null),
            Stateproofs: new ResourceAvailability(Disabled: !isArchive, OldestBlock: isArchive ? 0L : null));
    }
}
