// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Db.LogIndex;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>MCP tools about the node itself rather than chain data.</summary>
/// <remarks>
/// <c>node_status</c> reads only in-process services (block tree, sync state, peer pool, configuration) and needs no RPC
/// module, so it runs through <see cref="McpToolExecutor.ExecuteLocalAsync"/> and holds a concurrency slot only for the
/// few property reads it makes. Each service read is guarded: a service that is missing or fails yields
/// <see langword="null"/> fields instead of failing the tool.
/// </remarks>
[McpServerToolType]
internal sealed class McpNodeTools(
    McpToolExecutor executor,
    McpNodeCapabilities capabilities,
    McpChainProfile chainProfile,
    IBlockTree blockTree,
    IRpcModuleProvider rpcModuleProvider,
    ISyncConfig syncConfig,
    IMcpConfig config,
    ILogManager logManager,
    IEthSyncingInfo? syncingInfo = null,
    ISyncPeerPool? peerPool = null,
    ISyncPointers? syncPointers = null,
    ILogIndexStorage? logIndexStorage = null,
    ITimestamper? timestamper = null) : IMcpToolSet
{
    /// <summary>A head older than this is reported as a warning (several missed slots on every supported chain).</summary>
    internal const long StaleHeadSeconds = 120;

    private const int MaxListedModules = 32;

    private const string NodeStatusSchema = """
        {
          "type": "object",
          "properties": {
            "result": {
              "type": "object",
              "properties": {
                "chain": {
                  "type": "object",
                  "properties": {
                    "chainId": { "type": "string", "description": "0x-prefixed hex" },
                    "chainIdDecimal": { "type": "integer" },
                    "networkName": { "type": "string" },
                    "nativeCurrency": { "type": "string" }
                  },
                  "required": ["chainId", "chainIdDecimal", "networkName", "nativeCurrency"]
                },
                "client": {
                  "type": "object",
                  "properties": {
                    "name": { "type": "string" },
                    "version": { "type": "string" },
                    "clientId": { "type": "string" }
                  },
                  "required": ["name", "version", "clientId"]
                },
                "head": {
                  "type": ["object", "null"],
                  "properties": {
                    "number": { "type": "integer" },
                    "numberHex": { "type": "string" },
                    "hash": { "type": ["string", "null"] },
                    "timestamp": { "type": "integer", "description": "Unix seconds" },
                    "timestampIso": { "type": "string" },
                    "ageSeconds": { "type": ["integer", "null"] }
                  },
                  "required": ["number", "numberHex", "hash", "timestamp", "timestampIso", "ageSeconds"]
                },
                "sync": {
                  "type": "object",
                  "properties": {
                    "isSyncing": { "type": "boolean" },
                    "modes": { "type": ["string", "null"], "description": "Active sync stages, e.g. WaitingForBlock, Full, FastHeaders, StateNodes" },
                    "highestBlock": { "type": ["integer", "null"] },
                    "lagBlocks": { "type": ["integer", "null"] },
                    "fastSync": { "type": "boolean" },
                    "snapSync": { "type": "boolean" },
                    "pivotBlock": { "type": ["integer", "null"] },
                    "lowestHeader": { "type": ["integer", "null"] },
                    "lowestBody": { "type": ["integer", "null"] },
                    "lowestReceipt": { "type": ["integer", "null"] },
                    "headStateAvailable": { "type": ["boolean", "null"] }
                  },
                  "required": ["isSyncing", "modes", "highestBlock", "lagBlocks", "fastSync", "snapSync", "headStateAvailable"]
                },
                "peers": {
                  "type": "object",
                  "properties": {
                    "count": { "type": ["integer", "null"] },
                    "max": { "type": ["integer", "null"] }
                  },
                  "required": ["count", "max"]
                },
                "state": {
                  "type": "object",
                  "properties": {
                    "backend": { "type": ["string", "null"], "description": "Flat, HalfPath or Hash" },
                    "archive": { "type": ["boolean", "null"] },
                    "summary": { "type": ["string", "null"] },
                    "oldestBlock": { "type": ["integer", "null"] },
                    "retentionBlocks": { "type": ["integer", "null"] }
                  },
                  "required": ["backend", "archive", "summary", "oldestBlock", "retentionBlocks"]
                },
                "history": {
                  "type": "object",
                  "properties": {
                    "oldestBodyBlock": { "type": ["integer", "null"] },
                    "oldestReceiptBlock": { "type": ["integer", "null"] },
                    "receiptsStored": { "type": "boolean" },
                    "retentionBlocks": { "type": ["integer", "null"] }
                  },
                  "required": ["oldestBodyBlock", "oldestReceiptBlock", "receiptsStored", "retentionBlocks"]
                },
                "features": {
                  "type": "object",
                  "properties": {
                    "trace": { "type": "boolean" },
                    "debug": { "type": "boolean" },
                    "logIndex": {
                      "type": "object",
                      "properties": {
                        "enabled": { "type": ["boolean", "null"] },
                        "fromBlock": { "type": ["integer", "null"] },
                        "toBlock": { "type": ["integer", "null"] }
                      },
                      "required": ["enabled", "fromBlock", "toBlock"]
                    },
                    "rpcModulesEnabled": { "type": ["array", "null"], "items": { "type": "string" } }
                  },
                  "required": ["trace", "debug", "logIndex", "rpcModulesEnabled"]
                },
                "warnings": { "type": "array", "items": { "type": "string" } }
              },
              "required": ["chain", "client", "head", "sync", "peers", "state", "history", "features", "warnings"]
            }
          },
          "required": ["result"]
        }
        """;

    private readonly ILogger _logger = logManager.GetClassLogger<McpNodeTools>();
    private readonly int _maxResultSize = Math.Max(1, config.MaxResultSize);

    /// <inheritdoc/>
    public IEnumerable<McpServerTool> CreateServerTools() => McpToolFactory.Create(this, static _ => string.Empty, _maxResultSize);

    /// <summary>Returns a health and capability summary of the node.</summary>
    [McpServerTool(Name = "node_status", Title = "Node status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Answers \"is my node healthy and synced, and what can it serve?\" in one call. Use it first when a user asks about their node, " +
        "or when another tool fails with unavailable. Returns: chain (chainId, networkName, nativeCurrency: ETH, or xDAI on Gnosis/Chiado); " +
        "client version; head block (number, hash, timestamp, timestampIso, ageSeconds); sync (isSyncing, modes, highestBlock, lagBlocks, " +
        "fast/snap sync pivot and backfill progress); peers (count, max); state storage (backend Flat/HalfPath/Hash, archive or pruned, oldest " +
        "block with state); history (oldest block with bodies and receipts, whether receipts are stored); features (trace/debug tools " +
        "available, log index range); and warnings: plain-English problems such as 0 peers, a stale head or an unfinished sync. " +
        "Block numbers are decimal integers except head.numberHex. Null means the value is unknown on this node. Takes no arguments.")]
    [McpToolOutputSchema(NodeStatusSchema)]
    public Task<CallToolResult> NodeStatus(CancellationToken cancellationToken) =>
        executor.ExecuteLocalAsync("node_status", _ => Task.FromResult(executor.Success(CollectStatus(), static (writer, status) =>
        {
            WriteStatus(writer, status);
            return null;
        })), cancellationToken);

    private NodeStatusSnapshot CollectStatus()
    {
        BlockHeader? head = TryRead(() => blockTree.Head?.Header);
        McpDataAvailability availability = capabilities.GetAvailability();
        SyncingResult? syncing = TryValue<SyncingResult>(() => syncingInfo?.GetFullInfo());
        bool isSyncing = syncing?.IsSyncing ?? availability.IsSyncing;
        string? modes = TryRead(() => syncingInfo?.SyncMode.ToFlagsString());

        ulong? headNumber = head?.Number;
        ulong? bestSuggested = TryValue<ulong>(() => blockTree.BestSuggestedHeader?.Number);
        ulong? highest = Max(Max(headNumber, bestSuggested), syncing is { IsSyncing: true } s ? s.HighestBlock : null);
        ulong? lag = highest is { } h && headNumber is { } n ? (h > n ? h - n : 0) : null;

        long? ageSeconds = null;
        if (head is not null)
        {
            DateTimeOffset now = TryValue<DateTimeOffset>(() => (timestamper ?? Timestamper.Default).UtcNowOffset) ?? DateTimeOffset.UtcNow;
            ageSeconds = Math.Max(0, now.ToUnixTimeSeconds() - (long)Math.Min(head.Timestamp, long.MaxValue));
        }

        bool? headStateAvailable = head is null ? null : capabilities.CheckState(head.Number) is null;
        int? peerCount = TryValue<int>(() => peerPool?.PeerCount);
        int? peerMax = TryValue<int>(() => peerPool?.PeerMaxCount);

        IReadOnlyCollection<string>? modules = TryRead(() => rpcModuleProvider.Enabled);
        string[]? enabledModules = modules?.OrderBy(static m => m, StringComparer.Ordinal).Take(MaxListedModules).ToArray();

        List<string> warnings = [];
        if (head is null)
        {
            warnings.Add("The node has no head block yet: it is starting up or has not synced any blocks.");
        }

        if (peerCount == 0)
        {
            warnings.Add("The node has 0 peers, so it cannot follow the chain. Check that the P2P port (30303 by default) is reachable and that bootnodes are configured.");
        }

        if (ageSeconds > StaleHeadSeconds)
        {
            warnings.Add($"The head block is {FormatAge(ageSeconds.Value)} old: the node may be stalled, still syncing, or its consensus client may be offline or not synced.");
        }

        if (isSyncing)
        {
            string behind = lag is { } l ? $", {l} blocks behind" : string.Empty;
            warnings.Add($"The node is still syncing ({modes ?? "unknown stage"}{behind}): recent data may be missing and historical queries may fail.");
        }

        if (headStateAvailable == false)
        {
            warnings.Add("State for the head block is not available yet: balance, call, code and storage queries will fail until state sync finishes.");
        }

        if (!availability.ReceiptsStored)
        {
            warnings.Add("Receipts are not stored (Receipt.StoreReceipts=false): receipt, log and transaction-status queries will fail.");
        }

        return new NodeStatusSnapshot(
            chainProfile.ChainId,
            chainProfile.NetworkName,
            chainProfile.NativeCurrencySymbol,
            head,
            ageSeconds,
            isSyncing,
            modes,
            highest,
            lag,
            syncConfig.FastSync,
            syncConfig.SnapSync,
            syncConfig.FastSync ? TryValue<ulong>(() => blockTree.SyncPivot.BlockNumber) : null,
            syncConfig.FastSync ? TryValue<ulong>(() => blockTree.LowestInsertedHeader?.Number) : null,
            syncConfig.FastSync ? TryValue<ulong>(() => syncPointers?.LowestInsertedBodyNumber) : null,
            syncConfig.FastSync ? TryValue<ulong>(() => syncPointers?.LowestInsertedReceiptBlockNumber) : null,
            headStateAvailable,
            peerCount,
            peerMax,
            availability,
            TryValue<bool>(() => rpcModuleProvider.Resolve("trace_transaction") is not null) ?? false,
            TryValue<bool>(() => rpcModuleProvider.Resolve("debug_traceTransaction") is not null) ?? false,
            TryValue<bool>(() => logIndexStorage?.Enabled),
            TryValue<int>(() => logIndexStorage?.MinBlockNumber),
            TryValue<int>(() => logIndexStorage?.MaxBlockNumber),
            enabledModules,
            warnings);

        static ulong? Max(ulong? a, ulong? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
    }

    private static void WriteStatus(Utf8JsonWriter writer, NodeStatusSnapshot s)
    {
        writer.WriteStartObject();

        writer.WriteStartObject("chain"u8);
        writer.WriteString("chainId"u8, $"0x{s.ChainId:x}");
        writer.WriteNumber("chainIdDecimal"u8, s.ChainId);
        writer.WriteString("networkName"u8, s.NetworkName);
        writer.WriteString("nativeCurrency"u8, s.NativeCurrency);
        writer.WriteEndObject();

        writer.WriteStartObject("client"u8);
        writer.WriteString("name"u8, ProductInfo.Name);
        writer.WriteString("version"u8, ProductInfo.Version);
        writer.WriteString("clientId"u8, ProductInfo.ClientId);
        writer.WriteEndObject();

        if (s.Head is { } head)
        {
            writer.WriteStartObject("head"u8);
            writer.WriteNumber("number"u8, head.Number);
            writer.WriteString("numberHex"u8, $"0x{head.Number:x}");
            writer.WriteString("hash"u8, head.Hash?.ToString());
            writer.WriteNumber("timestamp"u8, head.Timestamp);
            writer.WriteString("timestampIso"u8, FormatTimestamp(head.Timestamp));
            WriteNumberOrNull(writer, "ageSeconds"u8, s.AgeSeconds);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNull("head"u8);
        }

        writer.WriteStartObject("sync"u8);
        writer.WriteBoolean("isSyncing"u8, s.IsSyncing);
        writer.WriteString("modes"u8, s.Modes);
        WriteNumberOrNull(writer, "highestBlock"u8, s.HighestBlock);
        WriteNumberOrNull(writer, "lagBlocks"u8, s.LagBlocks);
        writer.WriteBoolean("fastSync"u8, s.FastSync);
        writer.WriteBoolean("snapSync"u8, s.SnapSync);
        WriteNumberOrNull(writer, "pivotBlock"u8, s.PivotBlock);
        WriteNumberOrNull(writer, "lowestHeader"u8, s.LowestHeader);
        WriteNumberOrNull(writer, "lowestBody"u8, s.LowestBody);
        WriteNumberOrNull(writer, "lowestReceipt"u8, s.LowestReceipt);
        WriteBooleanOrNull(writer, "headStateAvailable"u8, s.HeadStateAvailable);
        writer.WriteEndObject();

        writer.WriteStartObject("peers"u8);
        WriteNumberOrNull(writer, "count"u8, s.PeerCount);
        WriteNumberOrNull(writer, "max"u8, s.PeerMax);
        writer.WriteEndObject();

        McpDataAvailability a = s.Availability;
        writer.WriteStartObject("state"u8);
        writer.WriteString("backend"u8, a.Storage?.Backend);
        WriteBooleanOrNull(writer, "archive"u8, a.Storage?.Archive);
        writer.WriteString("summary"u8, a.Storage?.Summary);
        WriteNumberOrNull(writer, "oldestBlock"u8, a.OldestStateBlock);
        WriteNumberOrNull(writer, "retentionBlocks"u8, a.StateRetentionBlocks);
        writer.WriteEndObject();

        writer.WriteStartObject("history"u8);
        WriteNumberOrNull(writer, "oldestBodyBlock"u8, a.OldestBodyBlock);
        WriteNumberOrNull(writer, "oldestReceiptBlock"u8, a.OldestReceiptBlock);
        writer.WriteBoolean("receiptsStored"u8, a.ReceiptsStored);
        WriteNumberOrNull(writer, "retentionBlocks"u8, a.HistoryRetentionBlocks);
        writer.WriteEndObject();

        writer.WriteStartObject("features"u8);
        writer.WriteBoolean("trace"u8, s.TraceAvailable);
        writer.WriteBoolean("debug"u8, s.DebugAvailable);
        writer.WriteStartObject("logIndex"u8);
        WriteBooleanOrNull(writer, "enabled"u8, s.LogIndexEnabled);
        WriteNumberOrNull(writer, "fromBlock"u8, s.LogIndexEnabled == true ? s.LogIndexFrom : null);
        WriteNumberOrNull(writer, "toBlock"u8, s.LogIndexEnabled == true ? s.LogIndexTo : null);
        writer.WriteEndObject();
        if (s.EnabledModules is { } enabledModules)
        {
            writer.WriteStartArray("rpcModulesEnabled"u8);
            foreach (string module in enabledModules)
            {
                writer.WriteStringValue(module);
            }

            writer.WriteEndArray();
        }
        else
        {
            writer.WriteNull("rpcModulesEnabled"u8);
        }

        writer.WriteEndObject();

        writer.WriteStartArray("warnings"u8);
        foreach (string warning in s.Warnings)
        {
            writer.WriteStringValue(warning);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static string FormatTimestamp(ulong unixSeconds) =>
        McpEthHelpers.ToIso(unixSeconds) ?? unixSeconds.ToString(CultureInfo.InvariantCulture);

    private static string FormatAge(long seconds) => seconds switch
    {
        < 120 => $"{seconds} seconds",
        < 7200 => $"{seconds / 60} minutes",
        < 172800 => $"{seconds / 3600} hours",
        _ => $"{seconds / 86400} days"
    };

    private static void WriteNumberOrNull(Utf8JsonWriter writer, ReadOnlySpan<byte> name, long? value)
    {
        if (value is { } v) writer.WriteNumber(name, v);
        else writer.WriteNull(name);
    }

    private static void WriteNumberOrNull(Utf8JsonWriter writer, ReadOnlySpan<byte> name, ulong? value)
    {
        if (value is { } v) writer.WriteNumber(name, v);
        else writer.WriteNull(name);
    }

    private static void WriteBooleanOrNull(Utf8JsonWriter writer, ReadOnlySpan<byte> name, bool? value)
    {
        if (value is { } v) writer.WriteBoolean(name, v);
        else writer.WriteNull(name);
    }

    private T? TryRead<T>(Func<T?> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (_logger.IsDebug) _logger.Debug($"MCP node_status: a service read failed: {ex.Message}");
            return null;
        }
    }

    private T? TryValue<T>(Func<T?> read) where T : struct
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (_logger.IsDebug) _logger.Debug($"MCP node_status: a service read failed: {ex.Message}");
            return null;
        }
    }

    private sealed record NodeStatusSnapshot(
        ulong ChainId,
        string NetworkName,
        string NativeCurrency,
        BlockHeader? Head,
        long? AgeSeconds,
        bool IsSyncing,
        string? Modes,
        ulong? HighestBlock,
        ulong? LagBlocks,
        bool FastSync,
        bool SnapSync,
        ulong? PivotBlock,
        ulong? LowestHeader,
        ulong? LowestBody,
        ulong? LowestReceipt,
        bool? HeadStateAvailable,
        int? PeerCount,
        int? PeerMax,
        McpDataAvailability Availability,
        bool TraceAvailable,
        bool DebugAvailable,
        bool? LogIndexEnabled,
        int? LogIndexFrom,
        int? LogIndexTo,
        string[]? EnabledModules,
        List<string> Warnings);
}
