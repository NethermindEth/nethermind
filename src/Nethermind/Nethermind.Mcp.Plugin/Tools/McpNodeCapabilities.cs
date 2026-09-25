// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using ModelContextProtocol.Protocol;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Facade.Eth;
using Nethermind.History;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>What this node can serve right now: head, sync state and the oldest block with state, bodies and receipts.</summary>
/// <param name="HeadNumber">The current head block number.</param>
/// <param name="IsSyncing">Whether the node is still catching up with the network.</param>
/// <param name="OldestStateBlock">The oldest block whose state can be queried, or <see langword="null"/> if none (state not synced).</param>
/// <param name="OldestBodyBlock">The oldest block whose body (transactions) is stored, or <see langword="null"/> if unknown.</param>
/// <param name="OldestReceiptBlock">The oldest block whose receipts are stored, or <see langword="null"/> if unknown.</param>
public sealed record McpDataAvailability(long HeadNumber, bool IsSyncing, long? OldestStateBlock, long? OldestBodyBlock, long? OldestReceiptBlock)
{
    /// <summary>Gets how state is stored, or <see langword="null"/> if it could not be determined.</summary>
    public McpStateStorage? Storage { get; init; }

    /// <summary>Gets whether the node persists receipts (<c>Receipt.StoreReceipts</c>).</summary>
    public bool ReceiptsStored { get; init; } = true;

    /// <summary>Gets the rolling state window in blocks (in-memory trie pruning), or <see langword="null"/> without one.</summary>
    public long? StateRetentionBlocks { get; init; }

    /// <summary>Gets the rolling history-expiry window for bodies and receipts in blocks, or <see langword="null"/> without one.</summary>
    public long? HistoryRetentionBlocks { get; init; }
}

/// <summary>How the node stores world state.</summary>
/// <param name="Backend"><c>Flat</c>, <c>HalfPath</c> or <c>Hash</c>.</param>
/// <param name="Archive">Whether state of every block since the node's start of history is kept.</param>
/// <param name="Summary">A short human-readable description, such as <c>pruned, HalfPath</c>.</param>
public sealed record McpStateStorage(string Backend, bool Archive, string Summary);

/// <summary>Reports which blocks this node can serve so tools can fail with errors that name the available range.</summary>
/// <remarks>
/// <para>
/// Every <c>Check*</c> method returns <see langword="null"/> when the data is available <em>or when availability cannot be
/// determined</em> (unknown block, missing service, unexpected failure), so a check never blocks a query that could succeed;
/// the tool then fails later with the JSON-RPC module's own error, which <see cref="McpToolExecutor"/> maps as well.
/// </para>
/// <para>
/// The checks use the same cheap, authoritative lookups the RPC modules use before doing the work:
/// <see cref="IStateReader.HasStateForBlock"/> for state (the guard of <c>eth_call</c>/<c>eth_getBalance</c>), block-store
/// and receipt-store key lookups for bodies and receipts. Ranges in messages and in <see cref="GetAvailability"/> come from
/// <see cref="IEthCapabilitiesProvider"/> (<c>eth_capabilities</c>: sync pivot, ancient barriers, history expiry, trie
/// pruning window), adjusted for flat-state history, and are informational: state availability is not monotonic (a HalfPath
/// node may still hold an old checkpoint root, a flat node serves only its persisted block plus the in-memory snapshots above
/// it), so the range is never used to reject a query on its own.
/// </para>
/// </remarks>
public sealed class McpNodeCapabilities(
    IBlockTree blockTree,
    ISyncConfig syncConfig,
    IReceiptConfig receiptConfig,
    IPruningConfig pruningConfig,
    IFlatDbConfig flatDbConfig,
    IInitConfig initConfig,
    ILogManager logManager,
    IEthCapabilitiesProvider? capabilitiesProvider = null,
    IStateReader? stateReader = null,
    IReceiptStorage? receiptStorage = null,
    IWorldStateManager? worldStateManager = null,
    IEthSyncingInfo? syncingInfo = null,
    IHistoryPruner? historyPruner = null,
    Lazy<INodeStorageFactory>? nodeStorageFactory = null)
{
    // Availability is read on every failing check and by node_status; a short cache keeps bursts cheap while staying fresh.
    private const long CacheMilliseconds = 1000;
    private const string FlatBackend = "Flat";

    private readonly ILogger _logger = logManager.GetClassLogger<McpNodeCapabilities>();
    private readonly Lock _lock = new();
    private McpDataAvailability? _cached;
    private long _cachedAt;
    private McpStateStorage? _storage;

    /// <summary>Gets the current data availability; values are cached for about a second.</summary>
    public McpDataAvailability GetAvailability()
    {
        long now = Environment.TickCount64;
        lock (_lock)
        {
            if (_cached is not null && now - _cachedAt < CacheMilliseconds)
            {
                return _cached;
            }
        }

        McpDataAvailability availability = ComputeAvailability();
        lock (_lock)
        {
            _cached = availability;
            _cachedAt = now;
        }

        return availability;
    }

    /// <summary>Gets how the node stores state, or <see langword="null"/> if it cannot be determined.</summary>
    public McpStateStorage? GetStateStorage() => _storage ??= Try(DetectStorage);

    /// <summary>Returns an <c>unavailable</c> error naming the available state range, or <see langword="null"/> if state at <paramref name="blockNumber"/> is available.</summary>
    public CallToolResult? CheckState(long blockNumber) => blockNumber < 0 ? null : CheckState((ulong)blockNumber);

    /// <inheritdoc cref="CheckState(long)"/>
    public CallToolResult? CheckState(ulong blockNumber)
    {
        BlockHeader? header = FindCanonicalHeader(blockNumber);
        if (header is null || stateReader is null || TryValue<bool>(() => stateReader.HasStateForBlock(header)) != false)
        {
            return null;
        }

        McpDataAvailability availability = GetAvailability();
        string advice = availability.OldestStateBlock is null && availability.IsSyncing
            ? "Retry after the node finishes syncing, or query another node."
            : "Use a recent block (for example \"latest\"), or query an archive node.";
        return McpToolExecutor.Error(McpToolErrorCodes.Unavailable,
            $"State for block {blockNumber} is not available on this node: it {DescribeStateRange(availability)}. {advice}");
    }

    /// <summary>Returns an <c>unavailable</c> error naming the available body range, or <see langword="null"/> if the body of <paramref name="blockNumber"/> is available.</summary>
    public CallToolResult? CheckBody(long blockNumber) => blockNumber < 0 ? null : CheckBody((ulong)blockNumber);

    /// <inheritdoc cref="CheckBody(long)"/>
    public CallToolResult? CheckBody(ulong blockNumber)
    {
        BlockHeader? header = FindCanonicalHeader(blockNumber);
        if (header?.Hash is not { } hash || TryValue<bool>(() => blockTree.HasBlock(blockNumber, hash)) != false)
        {
            return null;
        }

        return McpToolExecutor.Error(McpToolErrorCodes.Unavailable,
            $"The body (transactions) of block {blockNumber} is not stored on this node: it {DescribeHistory(blockNumber, receipts: false, GetAvailability())}. " +
            "Use a more recent block, or query a node that keeps full history.");
    }

    /// <summary>Returns an <c>unavailable</c> error naming the available receipt range, or <see langword="null"/> if receipts of <paramref name="blockNumber"/> are available.</summary>
    public CallToolResult? CheckReceipts(long blockNumber) => blockNumber < 0 ? null : CheckReceipts((ulong)blockNumber);

    /// <inheritdoc cref="CheckReceipts(long)"/>
    public CallToolResult? CheckReceipts(ulong blockNumber)
    {
        // Derived receipts are recomputed from state on demand, so a missing stored body proves nothing.
        if (receiptConfig.DeriveFromState)
        {
            return null;
        }

        BlockHeader? header = FindCanonicalHeader(blockNumber);
        // A block without transactions has no receipts to miss.
        if (header?.Hash is not { } hash || header.TxRoot == Keccak.EmptyTreeHash)
        {
            return null;
        }

        if (!receiptConfig.StoreReceipts)
        {
            return McpToolExecutor.Error(McpToolErrorCodes.Unavailable,
                $"Receipts and logs of block {blockNumber} are not available: this node does not store receipts (Receipt.StoreReceipts=false). Query another node.");
        }

        if (receiptStorage is null || TryValue<bool>(() => receiptStorage.HasBlock(blockNumber, hash)) != false)
        {
            return null;
        }

        return McpToolExecutor.Error(McpToolErrorCodes.Unavailable,
            $"Receipts and logs of block {blockNumber} are not stored on this node: it {DescribeHistory(blockNumber, receipts: true, GetAvailability())}. " +
            "Use a more recent block, or query a node that keeps full history.");
    }

    /// <summary>Describes the state range as a predicate of "the node", such as <c>keeps state for blocks 20000000..20000128 (pruned, HalfPath)</c>.</summary>
    public static string DescribeStateRange(McpDataAvailability availability)
    {
        string storage = availability.Storage is { } s ? $" ({s.Summary})" : string.Empty;
        if (availability.OldestStateBlock is not { } oldest)
        {
            return availability.IsSyncing
                ? $"is still syncing and has no queryable state yet{storage}"
                : $"has no queryable state yet{storage}";
        }

        return $"keeps state for blocks {oldest}..{availability.HeadNumber}{storage}";
    }

    /// <summary>Describes the stored body and receipt ranges as a predicate of "the node", for failures that do not say which data is missing.</summary>
    public static string DescribeHistoryRange(McpDataAvailability availability)
    {
        string bodies = availability.OldestBodyBlock is { } b ? $"bodies from block {b}" : "bodies from an unknown block";
        string receipts = !availability.ReceiptsStored
            ? "no receipts (Receipt.StoreReceipts=false)"
            : availability.OldestReceiptBlock is { } r ? $"receipts from block {r}" : "receipts from an unknown block";
        return $"keeps block {bodies} and {receipts} up to head {availability.HeadNumber}";
    }

    private string DescribeHistory(ulong blockNumber, bool receipts, McpDataAvailability availability)
    {
        long? oldest = receipts ? availability.OldestReceiptBlock : availability.OldestBodyBlock;
        string what = receipts ? "receipts" : "bodies";
        string range = oldest is { } o ? $"keeps {what} for blocks {o}..{availability.HeadNumber}" : $"does not report its oldest stored {what}";

        string? reason = null;
        if (TryValue<ulong>(() => historyPruner?.OldestBlockHeader?.Number) is { } expiredBelow && blockNumber < expiredBelow)
        {
            reason = "history expiry (EIP-4444, History.Pruning) removed older blocks";
        }
        else if (syncConfig.FastSync && syncConfig.PivotNumber > 0 && blockNumber < syncConfig.PivotNumber)
        {
            bool downloads = receipts ? syncConfig.DownloadReceiptsInFastSync : syncConfig.DownloadBodiesInFastSync;
            ulong barrier = receipts ? syncConfig.AncientReceiptsBarrierCalc : syncConfig.AncientBodiesBarrierCalc;
            if (!downloads)
            {
                reason = $"fast sync was configured not to download old {what} (Sync.Download{(receipts ? "Receipts" : "Bodies")}InFastSync=false)";
            }
            else if (blockNumber < barrier)
            {
                reason = $"fast sync skips {what} below block {barrier} (Sync.Ancient{(receipts ? "Receipts" : "Bodies")}Barrier)";
            }
            else if (availability.IsSyncing)
            {
                reason = $"old {what} are still being downloaded";
            }
        }

        return reason is null ? range : $"{range}; {reason}";
    }

    private McpDataAvailability ComputeAvailability()
    {
        BlockHeader? head = Try(() => blockTree.Head?.Header);
        long headNumber = head is null ? 0 : ToLong(head.Number);
        bool isSyncing = TryValue<bool>(() => syncingInfo?.IsSyncing()) ?? false;
        EthCapabilities? capabilities = capabilitiesProvider is null ? null : Try(capabilitiesProvider.GetCapabilities);
        McpStateStorage? storage = GetStateStorage();

        long? oldestState = Oldest(capabilities?.State);
        // eth_capabilities reports the flat persisted block as the floor; flat history serves older blocks too.
        if (storage?.Backend == FlatBackend && flatDbConfig.HistoryEnabled)
        {
            long historyFloor = flatDbConfig.HistoryRetention switch
            {
                HistoryRetentionMode.Rolling => Math.Max(0, headNumber - ToLong(flatDbConfig.HistoryRetentionBlocks)),
                HistoryRetentionMode.SinceBlock => ToLong(flatDbConfig.HistoryRetentionSinceBlock),
                _ => syncConfig.FastSync ? ToLong(TryValue<ulong>(() => blockTree.SyncPivot.BlockNumber) ?? 0) : 0
            };
            oldestState = oldestState is { } o ? Math.Min(o, historyFloor) : historyFloor;
        }

        return new McpDataAvailability(headNumber, isSyncing, oldestState, Oldest(capabilities?.Blocks),
            receiptConfig.StoreReceipts ? Oldest(capabilities?.Receipts) : null)
        {
            Storage = storage,
            ReceiptsStored = receiptConfig.StoreReceipts,
            StateRetentionBlocks = capabilities?.State.DeleteStrategy is { } stateWindow ? ToLong(stateWindow.RetentionBlocks) : null,
            HistoryRetentionBlocks = capabilities?.Blocks.DeleteStrategy is { } historyWindow ? ToLong(historyWindow.RetentionBlocks) : null,
        };

        static long? Oldest(ResourceAvailability? resource) =>
            resource is { Disabled: false, OldestBlock: { } oldest } ? ToLong(oldest) : null;
    }

    private McpStateStorage DetectStorage()
    {
        if (worldStateManager is FlatWorldStateManager)
        {
            if (!flatDbConfig.HistoryEnabled)
            {
                return new McpStateStorage(FlatBackend, false, "pruned, Flat: recent blocks only");
            }

            return flatDbConfig.HistoryRetention switch
            {
                HistoryRetentionMode.Rolling => new McpStateStorage(FlatBackend, false, $"Flat with a {flatDbConfig.HistoryRetentionBlocks}-block history window"),
                HistoryRetentionMode.SinceBlock => new McpStateStorage(FlatBackend, false, $"Flat with history since block {flatDbConfig.HistoryRetentionSinceBlock}"),
                _ => new McpStateStorage(FlatBackend, true, "archive, Flat history")
            };
        }

        INodeStorage.KeyScheme preferred = initConfig.StateDbKeyScheme;
        INodeStorage.KeyScheme scheme = TryValue<INodeStorage.KeyScheme>(() => nodeStorageFactory?.Value.CurrentKeyScheme)
            ?? (preferred == INodeStorage.KeyScheme.Current ? INodeStorage.KeyScheme.HalfPath : preferred);
        string backend = scheme == INodeStorage.KeyScheme.Hash ? "Hash" : "HalfPath";
        PruningMode mode = pruningConfig.Mode;
        return mode == PruningMode.None
            ? new McpStateStorage(backend, true, $"archive, {backend}")
            : new McpStateStorage(backend, false, mode.IsMemory()
                ? $"pruned, {backend}: about the last {pruningConfig.PruningBoundary} blocks"
                : $"pruned, {backend}: full pruning");
    }

    private BlockHeader? FindCanonicalHeader(ulong blockNumber) =>
        Try(() => blockTree.FindHeader(blockNumber, BlockTreeLookupOptions.RequireCanonical));

    private T? Try<T>(Func<T?> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (_logger.IsDebug) _logger.Debug($"MCP node capabilities: a service read failed: {ex.Message}");
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
            if (_logger.IsDebug) _logger.Debug($"MCP node capabilities: a service read failed: {ex.Message}");
            return null;
        }
    }

    private static long ToLong(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;
}
