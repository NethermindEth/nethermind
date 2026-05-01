// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.JsonRpc.Modules.Eth;

internal class EthCapabilitiesProvider(
    IBlockFinder blockFinder,
    ISyncConfig? syncConfig,
    IPruningConfig? pruningConfig)
{
    public EthCapabilitiesResult GetCapabilities()
    {
        BlockHeader? head = blockFinder.Head?.Header;
        long headNumber = head?.Number ?? 0;
        Hash256 headHash = head?.Hash ?? Keccak.Zero;

        IBlockTree? blockTree = blockFinder as IBlockTree;
        bool headersAvailable = blockTree?.BestSuggestedHeader is not null;
        long lowestBlock = blockTree?.LowestInsertedHeader?.Number ?? 0;

        bool receiptsSynced = syncConfig?.DownloadReceiptsInFastSync ?? true;
        // AncientReceiptsBarrierCalc returns Math.Max(1, …) which is wrong for archive nodes
        // (PivotNumber = 0). When there was no fast sync, receipts are available from genesis.
        long oldestReceipts = (syncConfig?.PivotNumber ?? 0) == 0
            ? 0
            : syncConfig!.AncientReceiptsBarrierCalc;

        PruningMode mode = pruningConfig?.Mode ?? PruningMode.None;
        bool isArchive = mode == PruningMode.None;
        // Memory (and Hybrid) pruning maintains a rolling window of PruningBoundary recent states.
        // Full-only pruning is periodic and non-linear — we cannot claim a predictable oldest block.
        long? retentionBlocks = mode.IsMemory() ? pruningConfig!.PruningBoundary : null;
        long? stateOldest = isArchive ? 0L
            : mode.IsMemory() && head is not null ? Math.Max(0L, head.Number - retentionBlocks!.Value)
            : null;

        CapabilityDeleteStrategy? windowStrategy = retentionBlocks is > 0
            ? new CapabilityDeleteStrategy("window", retentionBlocks.Value)
            : null;

        CapabilityResource receiptResource(bool synced) =>
            new(Disabled: !synced, OldestBlock: synced ? oldestReceipts : null);

        return new EthCapabilitiesResult(
            Head: new CapabilityHead(headNumber, headHash),
            // State is always available (at minimum the genesis trie exists), even on a
            // memory-pruned node that hasn't yet synced past the first block — in which case
            // OldestBlock is null because the rolling window lower bound cannot yet be computed.
            State: new CapabilityResource(Disabled: false, OldestBlock: stateOldest, DeleteStrategy: windowStrategy),
            Tx: receiptResource(receiptsSynced),
            Logs: receiptResource(receiptsSynced),
            Receipts: receiptResource(receiptsSynced),
            Blocks: new CapabilityResource(Disabled: !headersAvailable, OldestBlock: headersAvailable ? lowestBlock : null),
            Stateproofs: new CapabilityResource(Disabled: !isArchive, OldestBlock: isArchive ? 0L : null));
    }
}
