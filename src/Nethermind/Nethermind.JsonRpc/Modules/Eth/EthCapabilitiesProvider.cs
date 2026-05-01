// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Extensions;
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
        string headNumber = head is not null ? head.Number.ToHexString(skipLeadingZeros: true) : "0x0";
        string headHash = head?.Hash?.ToString() ?? "0x0000000000000000000000000000000000000000000000000000000000000000";

        IBlockTree? blockTree = blockFinder as IBlockTree;
        bool headersAvailable = blockTree?.BestSuggestedHeader is not null;
        long lowestBlock = blockTree?.LowestInsertedHeader?.Number ?? 0;
        string lowestBlockHex = lowestBlock.ToHexString(skipLeadingZeros: true);

        bool receiptsSynced = syncConfig?.DownloadReceiptsInFastSync ?? true;
        // AncientReceiptsBarrierCalc returns Math.Max(1, …) which is wrong for archive nodes
        // (PivotNumber = 0). When there was no fast sync, receipts are available from genesis.
        long oldestReceipts = (syncConfig?.PivotNumber ?? 0) == 0
            ? 0
            : syncConfig!.AncientReceiptsBarrierCalc;
        string oldestReceiptsHex = oldestReceipts.ToHexString(skipLeadingZeros: true);

        PruningMode mode = pruningConfig?.Mode ?? PruningMode.None;
        bool isArchive = mode == PruningMode.None;
        // Memory (and Hybrid) pruning maintains a rolling window of PruningBoundary recent states.
        // Full-only pruning is periodic and non-linear — we cannot claim a predictable oldest block.
        long? retentionBlocks = mode.IsMemory() ? pruningConfig!.PruningBoundary : null;
        long? stateOldest = isArchive ? 0L
            : mode.IsMemory() && head is not null ? Math.Max(0L, head.Number - retentionBlocks!.Value)
            : null;
        string? stateOldestHex = stateOldest?.ToHexString(skipLeadingZeros: true);

        CapabilityDeleteStrategy? windowStrategy = retentionBlocks is > 0
            ? new CapabilityDeleteStrategy { Type = "window", RetentionBlocks = retentionBlocks.Value }
            : null;

        CapabilityResource receiptResource(bool synced) => new()
        {
            Disabled = !synced,
            OldestBlock = synced ? oldestReceiptsHex : null
        };

        return new EthCapabilitiesResult
        {
            Head = new CapabilityHead { Number = headNumber, Hash = headHash },
            Blocks = new CapabilityResource
            {
                Disabled = !headersAvailable,
                OldestBlock = headersAvailable ? lowestBlockHex : null
            },
            State = new CapabilityResource
            {
                // State is always available (at minimum the genesis trie exists), even on a
                // memory-pruned node that hasn't yet synced past the first block — in which case
                // OldestBlock is null because the rolling window lower bound cannot yet be computed.
                Disabled = false,
                OldestBlock = stateOldestHex,
                DeleteStrategy = windowStrategy
            },
            Tx = receiptResource(receiptsSynced),
            Logs = receiptResource(receiptsSynced),
            Receipts = receiptResource(receiptsSynced),
            Stateproofs = new CapabilityResource
            {
                Disabled = !isArchive,
                OldestBlock = isArchive ? "0x0" : null
            }
        };
    }
}
