// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Mcp.Dto;

namespace Nethermind.Mcp.Adapter;

public sealed class NethermindNodeAdapter(INethermindApi api) : INethermindNodeAdapter
{
    private readonly INethermindApi _api = api;

    public NodeVersionDto GetNodeVersion() => new(
        ClientVersion: ProductInfo.ClientId,
        DotNetRuntime: RuntimeInformation.FrameworkDescription,
        OperatingSystem: RuntimeInformation.OSDescription,
        EnabledRpcModules: Array.Empty<string>());

    public SyncStatusDto GetSyncStatus()
    {
        long current = _api.BlockTree?.Head?.Number ?? 0;
        long highest = _api.BlockTree?.BestSuggestedHeader?.Number ?? current;
        long behind = Math.Max(0, highest - current);
        int peerCount = _api.SyncPeerPool?.PeerCount ?? 0;
        string mode = behind == 0 ? "Idle" : "Syncing";

        return new SyncStatusDto(current, highest, mode, behind, peerCount);
    }

    public NodeHealthDto GetNodeHealth()
    {
        SyncStatusDto sync = GetSyncStatus();

        string syncStatus = sync.BlocksBehind <= 1 ? "Healthy" : "Degraded";
        string peersStatus = sync.PeerCount switch
        {
            0 => "Unhealthy",
            >= 1 and <= 4 => "Degraded",
            _ => "Healthy",
        };
        string memoryStatus = "Healthy";

        HealthCheckDto[] checks =
        [
            new("sync", syncStatus, $"{sync.BlocksBehind} blocks behind", sync.BlocksBehind),
            new("peers", peersStatus, $"{sync.PeerCount} peers connected", sync.PeerCount),
            new("memory", memoryStatus, null, null),
        ];

        string overall = WorstStatus(syncStatus, peersStatus, memoryStatus);

        using Process process = Process.GetCurrentProcess();
        long uptimeSeconds = Math.Max(0, (long)(DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalSeconds);
        long memoryMb = process.WorkingSet64 / (1024 * 1024);

        return new NodeHealthDto(
            OverallStatus: overall,
            Checks: checks,
            UptimeSeconds: uptimeSeconds,
            ProcessMemoryMb: memoryMb,
            GcGen0Collections: GC.CollectionCount(0),
            GcGen1Collections: GC.CollectionCount(1),
            GcGen2Collections: GC.CollectionCount(2),
            DiskFreeGb: null,
            DiskUsedGb: null);
    }

    private static string WorstStatus(params string[] statuses)
    {
        if (statuses.Any(static s => s == "Unhealthy")) return "Unhealthy";
        if (statuses.Any(static s => s == "Degraded")) return "Degraded";
        return "Healthy";
    }

    public BlockSummaryDto? GetBlock(BlockParameter blockParameter)
    {
        ArgumentNullException.ThrowIfNull(blockParameter);

        Block? block = blockParameter.Type switch
        {
            BlockParameterType.Latest => _api.BlockTree?.Head,
            BlockParameterType.Earliest => _api.BlockTree is { } bt ? bt.FindBlock(bt.GenesisHash, BlockTreeLookupOptions.RequireCanonical) : null,
            BlockParameterType.Pending => _api.BlockTree is { PendingHash: { } pending } btP ? btP.FindBlock(pending, BlockTreeLookupOptions.None) : _api.BlockTree?.Head,
            BlockParameterType.Finalized => _api.BlockTree is { FinalizedHash: { } finalized } btF ? btF.FindBlock(finalized, BlockTreeLookupOptions.None) : null,
            BlockParameterType.Safe => _api.BlockTree is { SafeHash: { } safe } btS ? btS.FindBlock(safe, BlockTreeLookupOptions.None) : null,
            BlockParameterType.BlockNumber when blockParameter.BlockNumber is { } number =>
                _api.BlockTree?.FindBlock(number, blockParameter.RequireCanonical ? BlockTreeLookupOptions.RequireCanonical : BlockTreeLookupOptions.None),
            BlockParameterType.BlockHash when blockParameter.BlockHash is { } hash =>
                _api.BlockTree?.FindBlock(hash, blockParameter.RequireCanonical ? BlockTreeLookupOptions.RequireCanonical : BlockTreeLookupOptions.None),
            _ => null,
        };

        return Map(block);
    }

    private static BlockSummaryDto? Map(Block? block)
    {
        if (block is null) return null;

        BlockTransactionSummary[] txs = block.Transactions
            .Select(static tx => new BlockTransactionSummary(
                Hash: tx.Hash?.ToString() ?? string.Empty,
                From: tx.SenderAddress?.ToString() ?? string.Empty,
                To: tx.To?.ToString(),
                Value: tx.Value.ToString(),
                GasUsed: null))
            .ToArray();

        return new BlockSummaryDto(
            Number: block.Number,
            Hash: block.Hash?.ToString() ?? string.Empty,
            ParentHash: block.ParentHash?.ToString() ?? string.Empty,
            Timestamp: (long)block.Timestamp,
            GasUsed: block.GasUsed,
            GasLimit: block.GasLimit,
            BaseFeePerGas: block.BaseFeePerGas.ToString(),
            TransactionCount: txs.Length,
            FeeRecipient: block.Beneficiary?.ToString() ?? string.Empty,
            StateRoot: block.StateRoot?.ToString() ?? string.Empty,
            Size: 0,
            Transactions: txs);
    }
}
