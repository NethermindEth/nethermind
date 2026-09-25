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
/// and receipt-store key lookups for bodies and receipts. The oldest stored body and receipt blocks are found by
/// binary-searching those same key lookups between block 1 and the head (see <see cref="ProbeHistoryFloors"/>), which is right
/// however the node synced: <c>eth_capabilities</c> reports bodies and receipts as disabled on a node that fast-synced without
/// old history, and the sync pivot moves with every pivot update although the stored range does not. State ranges come
/// from <see cref="IEthCapabilitiesProvider"/> (<c>eth_capabilities</c>: state floor, trie pruning window), adjusted for
/// flat-state history. Ranges are informational:
/// state availability is not monotonic (a HalfPath
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
    Lazy<INodeStorageFactory>? nodeStorageFactory = null,
    IHistoryConfig? historyConfig = null)
{
    // Availability is read on every failing check and by node_status; a short cache keeps bursts cheap while staying fresh.
    private const long CacheMilliseconds = 1000;

    // The body and receipt floors move only with history expiry or a backfill, so the probe (about 50 key lookups) runs
    // at most every five minutes, and sooner only when a check finds a block missing above the cached floor.
    private const long FloorCacheMilliseconds = 5 * 60 * 1000;

    // A receipt probe of a block without transactions looks at most this many blocks up for one with transactions.
    private const int ReceiptProbeSpan = 1024;
    private const string FlatBackend = "Flat";

    private readonly ILogger _logger = logManager.GetClassLogger<McpNodeCapabilities>();
    private readonly Lock _lock = new();
    private McpDataAvailability? _cached;
    private long _cachedAt;
    private McpStateStorage? _storage;
    private HistoryFloors? _floors;
    private long _floorsAt;

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
            $"The body (transactions) of block {blockNumber} is not stored on this node: it {DescribeHistory(blockNumber, receipts: false, AvailabilityBelow(blockNumber, receipts: false))}. " +
            "Use a more recent block, or query a node that keeps full history.");
    }

    /// <summary>Returns an <c>unavailable</c> error naming the available receipt range, or <see langword="null"/> if receipts of <paramref name="blockNumber"/> are available.</summary>
    public CallToolResult? CheckReceipts(long blockNumber) => blockNumber < 0 ? null : CheckReceipts((ulong)blockNumber);

    /// <inheritdoc cref="CheckReceipts(long)"/>
    public CallToolResult? CheckReceipts(ulong blockNumber) =>
        FindCanonicalHeader(blockNumber) is { } header ? CheckReceipts(header) : null;

    /// <summary>Returns an <c>unavailable</c> error naming the available receipt range, or <see langword="null"/> if receipts of the block of <paramref name="header"/> are available.</summary>
    /// <param name="header">The canonical header of the block.</param>
    internal CallToolResult? CheckReceipts(BlockHeader header)
    {
        // Derived receipts are recomputed from state on demand, so a missing stored body proves nothing.
        if (receiptConfig.DeriveFromState)
        {
            return null;
        }

        ulong blockNumber = header.Number;
        // A block without transactions has no receipts to miss.
        if (header.Hash is not { } hash || header.TxRoot == Keccak.EmptyTreeHash)
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
            $"Receipts and logs of block {blockNumber} are not stored on this node: it {DescribeHistory(blockNumber, receipts: true, AvailabilityBelow(blockNumber, receipts: true))}. " +
            "Use a more recent block, or query a node that keeps full history.");
    }

    /// <summary>
    /// Returns a sentence for a transaction hash that was not found, pointing out that it may predate this node's history
    /// (ancient barriers, history expiry), or an empty string when the node keeps full history or its range is unknown.
    /// </summary>
    /// <remarks>Hash lookups need both the block body and the transaction index kept with the receipts, so the later floor applies.</remarks>
    public string DescribeTransactionHistoryLimit()
    {
        McpDataAvailability availability = GetAvailability();
        long oldest = Math.Max(availability.OldestBodyBlock ?? 0, availability.OldestReceiptBlock ?? 0);
        // Sync never inserts the genesis body, so a floor of block 1 still means full history.
        return oldest > 1
            ? $" This node keeps transaction history only from block {oldest} (see node_status); older transactions cannot be found here."
            : string.Empty;
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

    /// <summary>
    /// Returns the availability for a block just found missing; a cached floor at or below that block is stale (a history
    /// expiry pass ran since), so the floors are probed again.
    /// </summary>
    private McpDataAvailability AvailabilityBelow(ulong missingBlock, bool receipts)
    {
        McpDataAvailability availability = GetAvailability();
        long? floor = receipts ? availability.OldestReceiptBlock : availability.OldestBodyBlock;
        if (floor is not { } f || ToLong(missingBlock) < f)
        {
            return availability;
        }

        lock (_lock)
        {
            _floors = null;
            _cached = null;
        }

        return GetAvailability();
    }

    private string DescribeHistory(ulong blockNumber, bool receipts, McpDataAvailability availability)
    {
        long? oldest = receipts ? availability.OldestReceiptBlock : availability.OldestBodyBlock;
        string what = receipts ? "receipts" : "bodies";
        string range = oldest is { } o ? $"keeps {what} for blocks {o}..{availability.HeadNumber}" : $"could not determine its oldest stored {what}";
        string? reason = HistoryReason(blockNumber, receipts, oldest, availability.IsSyncing);
        return reason is null ? range : $"{range}; {reason}";
    }

    /// <summary>Explains why the <paramref name="receipts"/> or body of a block below the stored range is missing, or returns <see langword="null"/> when no configuration explains it.</summary>
    internal string? HistoryReason(ulong blockNumber, bool receipts, long? oldest, bool isSyncing)
    {
        string what = receipts ? "receipts" : "bodies";

        // The pruner reports an oldest block even with expiry disabled (the first stored body it found), so it is named
        // as the cause only when history pruning is configured.
        if (historyConfig?.Enabled() == true
            && TryValue<ulong>(() => historyPruner?.OldestBlockHeader?.Number) is { } expiredBelow && blockNumber < expiredBelow)
        {
            return $"history expiry (EIP-4444, History.Pruning={historyConfig.Pruning}) removed blocks below {expiredBelow}";
        }

        // Below the probed floor only: a block missing inside the stored range is not explained by how the node synced.
        if (!syncConfig.FastSync || (oldest is { } floor && ToLong(blockNumber) >= floor))
        {
            return null;
        }

        List<string> skipped = new(2);
        if (!syncConfig.DownloadBodiesInFastSync) skipped.Add("Sync.DownloadBodiesInFastSync=false");
        if (receipts && !syncConfig.DownloadReceiptsInFastSync) skipped.Add("Sync.DownloadReceiptsInFastSync=false");
        if (skipped.Count > 0)
        {
            return $"this node was snap/fast-synced and did not download older {what} ({string.Join(", ", skipped)})";
        }

        ulong barrier = receipts ? syncConfig.AncientReceiptsBarrierCalc : syncConfig.AncientBodiesBarrierCalc;
        if (barrier > 1 && blockNumber < barrier)
        {
            return $"fast sync does not download {what} below the ancient barrier, block {barrier} (Sync.Ancient{(receipts ? "Receipts" : "Bodies")}Barrier)";
        }

        return isSyncing ? $"older {what} are still being downloaded" : null;
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

        HistoryFloors floors = GetHistoryFloors(head);
        long? oldestBody = floors.Body ?? Oldest(capabilities?.Blocks);
        long? oldestReceipt = !receiptConfig.StoreReceipts ? null
            : receiptConfig.DeriveFromState ? oldestBody
            : floors.Receipt ?? Oldest(capabilities?.Receipts);
        long? stateRetention = capabilities?.State.DeleteStrategy is { } stateWindow ? ToLong(stateWindow.RetentionBlocks) : null;

        // The detected summary names the configured boundary; the window actually kept is what eth_capabilities reports.
        if (storage is { Archive: false } && storage.Backend != FlatBackend && pruningConfig.Mode.IsMemory() && stateRetention is { } retention)
        {
            storage = storage with { Summary = $"pruned, {storage.Backend}: about the last {retention} blocks" };
        }

        return new McpDataAvailability(headNumber, isSyncing, oldestState, oldestBody, oldestReceipt)
        {
            Storage = storage,
            ReceiptsStored = receiptConfig.StoreReceipts,
            StateRetentionBlocks = stateRetention,
            HistoryRetentionBlocks = capabilities?.Blocks.DeleteStrategy is { } historyWindow ? ToLong(historyWindow.RetentionBlocks) : null,
        };

        static long? Oldest(ResourceAvailability? resource) =>
            resource is { Disabled: false, OldestBlock: { } oldest } ? ToLong(oldest) : null;
    }

    private HistoryFloors GetHistoryFloors(BlockHeader? head)
    {
        long now = Environment.TickCount64;
        lock (_lock)
        {
            if (_floors is { } cached && now - _floorsAt < FloorCacheMilliseconds)
            {
                return cached;
            }
        }

        HistoryFloors floors = head is null ? new HistoryFloors(null, null) : ProbeHistoryFloors(head.Number);
        lock (_lock)
        {
            _floors = floors;
            _floorsAt = now;
        }

        return floors;
    }

    /// <summary>Finds the oldest block whose body, and the oldest whose receipts, are stored, by binary search over the stores.</summary>
    /// <remarks>
    /// <para>
    /// Bodies and receipts are stored contiguously from a floor up to the head (sync and backfill insert them descending,
    /// history expiry deletes them ascending) while headers exist further down, so presence is monotonic and about 25 key
    /// lookups each find the floor on mainnet. Sync never inserts the genesis body, so the search starts at block 1 and a floor
    /// of 1 becomes 0 when genesis is stored too. A floor is <see langword="null"/> when the head itself is not stored or a
    /// lookup fails.
    /// </para>
    /// <para>
    /// A block without transactions may have no receipts stored (sync can skip it), so a receipt probe uses the first block at
    /// or above it that has transactions or a stored receipt entry: the receipt floor is the oldest block from which every
    /// block with transactions has its receipts. A run of empty blocks without entries counts as stored only when it reaches
    /// a block already found stored (or the head), which keeps the predicate monotonic; a longer run than
    /// <see cref="ReceiptProbeSpan"/> blocks counts as not stored, so on a synced chain with long empty runs the floor can come
    /// out too high, but never too low.
    /// </para>
    /// </remarks>
    private HistoryFloors ProbeHistoryFloors(ulong head)
    {
        long? body = TryValue<long>(() => LowestStored(1, head, (n, _) => BodyStored(n)) is { } floor ? ToLong(floor == 1 && BodyStored(0) ? 0 : floor) : null);
        if (receiptStorage is null || !receiptConfig.StoreReceipts || receiptConfig.DeriveFromState)
        {
            return new HistoryFloors(body, null);
        }

        // Receipts are never stored without their body.
        ulong from = body is { } b and > 1 ? (ulong)b : 1;
        long? receipt = TryValue<long>(() => LowestStored(from, head, ReceiptsStored) is { } floor
            ? ToLong(floor == 1 && body == 0 ? 0 : floor)
            : null);
        return new HistoryFloors(body, receipt);
    }

    /// <summary>Binary-searches the lowest block in [<paramref name="low"/>, <paramref name="high"/>] for which <paramref name="stored"/> holds.</summary>
    /// <param name="stored">Called with a block and the lowest block above it already found stored.</param>
    private static ulong? LowestStored(ulong low, ulong high, Func<ulong, ulong, bool> stored)
    {
        if (!stored(high, high))
        {
            return null;
        }

        while (low < high)
        {
            ulong middle = low + (high - low) / 2;
            if (stored(middle, high)) high = middle;
            else low = middle + 1;
        }

        return high;
    }

    private bool BodyStored(ulong number) =>
        blockTree.FindHeader(number, BlockTreeLookupOptions.RequireCanonical) is { Hash: { } hash } && blockTree.HasBlock(number, hash);

    private bool ReceiptsStored(ulong number, ulong storedAbove)
    {
        ulong last = Math.Min(storedAbove, number + ReceiptProbeSpan - 1);
        for (ulong n = number; n <= last; n++)
        {
            // Every block with transactions from here up is already known to be stored.
            if (n == storedAbove && n > number)
            {
                return true;
            }

            if (blockTree.FindHeader(n, BlockTreeLookupOptions.RequireCanonical) is not { Hash: { } hash } header)
            {
                return false;
            }

            // Block processing stores an entry for an empty block too, which history expiry deletes with the rest, so
            // one found proves the floor is at or below it; sync may skip empty blocks, so a missing one proves nothing.
            bool stored = receiptStorage!.HasBlock(n, hash);
            if (stored || header.TxRoot != Keccak.EmptyTreeHash)
            {
                return stored;
            }
        }

        // An empty head has no receipts to miss; an empty run too long to scan may hide a block without them.
        return number == storedAbove;
    }

    private readonly record struct HistoryFloors(long? Body, long? Receipt);

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
