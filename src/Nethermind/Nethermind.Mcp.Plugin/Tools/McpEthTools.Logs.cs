// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Filters;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Serialization.Json;

namespace Nethermind.Mcp.Plugin.Tools;

internal sealed partial class McpEthTools
{
    private const int LogCancellationCheckInterval = 64;

    /// <summary>The default number of logs per <c>get_logs</c> page, sized for LLM clients' tool-result limits.</summary>
    public const int DefaultLogPageLimit = 100;

    /// <summary>The default byte budget of a <c>get_logs</c> page (about 128 KB, roughly 35k tokens).</summary>
    public const int DefaultLogPageBytes = 128 * 1024;

    /// <summary>The smallest byte budget a caller may ask for.</summary>
    public const int MinLogPageBytes = 1024;

    /// <summary>Returns one page of logs matching a filter, with a cursor to the next page.</summary>
    /// <remarks>
    /// <para>
    /// Each page queries <c>eth_getLogs</c> for at most <see cref="IMcpConfig.MaxLogBlockRange"/> blocks, or up to
    /// <see cref="IMcpConfig.MaxIndexedLogBlockRange"/> blocks when the filter is selective (an address or topic) and the log
    /// index covers the page start, because the eth module then answers from the index instead of reading every block's
    /// receipts. The page then keeps at most <c>limit</c> logs (default <see cref="DefaultLogPageLimit"/>) and <c>maxBytes</c> bytes
    /// (default <see cref="DefaultLogPageBytes"/>), both capped by the node's limits.
    /// </para>
    /// <para>
    /// The eth module buffers the whole matching set and fails with <see cref="ErrorCodes.LimitExceeded"/> above
    /// <see cref="IJsonRpcConfig.MaxLogsPerResponse"/>; such a page is retried over half the block span until it fits.
    /// In logs stream mode the module instead stops silently at that count, so a page that reached it resumes right after
    /// its last log rather than at the next block.
    /// </para>
    /// </remarks>
    [McpServerTool(Name = "get_logs", Title = "Get logs", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Returns raw event logs (eth_getLogs format) in the inclusive block range [fromBlock, toBlock], one page at a time: " +
        "{logs: [...], fromBlock, toBlock (hex, the blocks this page actually covered), truncated (bool), nextCursor (string, only when truncated), indexed (bool)}. " +
        "Ranges of any size are accepted and scanned incrementally; when truncated is true, call get_logs again with exactly the same fromBlock, toBlock, address " +
        "and topics plus cursor=nextCursor to get the next page, until truncated is false. The cursor is opaque: do not parse or edit it, and it expires when the node restarts. " +
        "Up to 32 addresses and 4 topic positions are accepted; each topic position is null (any), a 32-byte hash, or an array of up to 32 hashes (any of them). " +
        "Filtering by address or topic is much faster, especially on nodes with the log index enabled. " +
        "If fromBlock is below the oldest block this node keeps receipts for, the scan starts at that block and the page reports clampedFromBlock (the requested start) and a note; " +
        "a range entirely below it fails with unavailable. A cursor becomes invalid if the chain reorganises past its position; then restart the query. " +
        "Pages are kept small by default (at most 100 logs and about 128 KB) so they fit in an LLM context; pass limit and maxBytes (up to this node's limits below) for larger pages. " +
        "To decode logs into named events (Transfer, Approval, ...) pass them to decode_logs, or use explain_transaction for a single transaction.")]
    [McpToolOutputSchema("""
        {"type":"object","required":["result"],"properties":{"result":{"type":"object","required":["logs","fromBlock","toBlock","truncated","indexed"],
          "properties":{
            "logs":{"type":"array","items":{"type":"object",
              "required":["address","blockHash","blockNumber","data","logIndex","removed","topics","transactionHash","transactionIndex"],
              "properties":{"address":{"type":"string"},"blockHash":{"type":"string"},"blockNumber":{"type":"string"},"blockTimestamp":{"type":"string"},
                "data":{"type":"string"},"logIndex":{"type":"string"},"removed":{"type":"boolean"},"topics":{"type":"array","items":{"type":"string"}},
                "transactionHash":{"type":"string"},"transactionIndex":{"type":"string"}}}},
            "fromBlock":{"type":"string"},"toBlock":{"type":"string"},"truncated":{"type":"boolean"},"nextCursor":{"type":"string"},"indexed":{"type":"boolean"},
            "clampedFromBlock":{"type":"string"},"note":{"type":"string"}}}}}
        """)]
    public Task<CallToolResult> GetLogs(
        [Description("First block of the range, inclusive. " + BlockSelectorDescription)] string fromBlock,
        [Description("Last block of the range, inclusive. " + BlockSelectorDescription)] string toBlock,
        [Description("Optional contract addresses (0x followed by 40 hex characters each) that emitted the logs; empty or omitted matches any address. At most 32.")] string[]? address = null,
        [Description("Optional topic filter by position (topic0 is usually the event signature hash, e.g. ERC-20 Transfer is " +
            "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef). Each position is null (any topic), " +
            "a 32-byte hash (0x followed by 64 hex characters), or an array of up to 32 such hashes (any of them). At most 4 positions.")] JsonElement[]? topics = null,
        [Description("Opaque nextCursor from a previous get_logs page with the same fromBlock, toBlock, address and topics; omit for the first page.")] string? cursor = null,
        [Description("Optional maximum number of logs in this page, from 1 up to the node's limit. Default 100.")] int? limit = null,
        [Description("Optional byte budget of this page's logs, from 1024 up to the node's limit. Default 131072 (128 KB).")] int? maxBytes = null,
        CancellationToken cancellationToken = default)
    {
        if (!McpToolInput.TryParseBlock(fromBlock, nameof(fromBlock), out BlockParameter? from, out string? error)
            || !McpToolInput.TryParseBlock(toBlock, nameof(toBlock), out BlockParameter? to, out error)
            || !McpToolInput.TryParseAddresses(address, nameof(address), out HashSet<AddressAsKey>? addresses, out error)
            || !McpToolInput.TryParseTopics(topics, nameof(topics), out Hash256[]?[]? topicFilter, out error))
        {
            return McpEthHelpers.InvalidInput(error);
        }

        if (limit is < 1 || limit > _maxLogs)
        {
            return McpEthHelpers.InvalidInput($"'limit' must be between 1 and {_maxLogs}.");
        }

        long maxPageBytes = MaxLogPageBytes;
        if (maxBytes is { } requestedBytes && (requestedBytes < Math.Min(MinLogPageBytes, maxPageBytes) || requestedBytes > maxPageBytes))
        {
            return McpEthHelpers.InvalidInput($"'maxBytes' must be between {Math.Min(MinLogPageBytes, maxPageBytes)} and {maxPageBytes}.");
        }

        byte[] filterHash = McpLogCursor.ComputeFilterHash(from, to, addresses, topicFilter);
        McpLogCursor? resume = null;
        if (cursor is not null)
        {
            if (!McpLogCursor.TryDecode(cursor, out error, out McpLogCursor decoded))
            {
                return McpEthHelpers.InvalidInput(error);
            }

            if (!decoded.Matches(filterHash))
            {
                return McpEthHelpers.InvalidInput("'cursor' was issued for a different query; pass exactly the same fromBlock, toBlock, address and topics " +
                    "as the call that returned it, or omit 'cursor' to start over.");
            }

            resume = decoded;
        }

        int pageLimit = limit ?? Math.Min(DefaultLogPageLimit, _maxLogs);
        long pageBytes = maxBytes ?? Math.Min(DefaultLogPageBytes, maxPageBytes);
        bool selective = addresses is { Count: > 0 } || HasTopic(topicFilter);

        return _executor.ExecuteAsync("get_logs", nameof(IEthRpcModule.eth_getLogs), (eth, token) =>
        {
            ulong start;
            ulong end;
            ulong startLogIndex = 0;
            ulong? clampedFrom = null;
            if (resume is { } position)
            {
                if (!position.IsAt(blockFinder.FindHeader(position.Block, BlockTreeLookupOptions.RequireCanonical)?.Hash))
                {
                    return Task.FromResult(McpToolExecutor.Error(McpToolErrorCodes.InvalidInput,
                        $"The chain reorganised since the previous page (block {position.Block} changed); restart the query without 'cursor'."));
                }

                (start, startLogIndex, end) = (position.Block, position.LogIndex, position.ToBlock);
            }
            else
            {
                // Resolve tags once so the page plan and the query see the same blocks even while the head moves.
                if (!McpEthHelpers.TryResolveBlockNumber(blockFinder, from, nameof(fromBlock), requireCanonical: true, out start, out CallToolResult? failure)
                    || !McpEthHelpers.TryResolveBlockNumber(blockFinder, to, nameof(toBlock), requireCanonical: true, out end, out failure))
                {
                    return Task.FromResult(failure);
                }

                if (start > end)
                {
                    return Task.FromResult(McpToolExecutor.Error(McpToolErrorCodes.InvalidInput, $"fromBlock ({start}) is greater than toBlock ({end})."));
                }

                // Blocks below the receipt floor have no logs to return; scanning them would only yield silent gaps.
                if (capabilities.GetAvailability().OldestReceiptBlock is { } floor && floor > 1 && start < (ulong)floor)
                {
                    if (end < (ulong)floor)
                    {
                        return Task.FromResult(McpToolExecutor.Error(McpToolErrorCodes.Unavailable,
                            $"Blocks {start}..{end} are older than this node's receipt history, which starts at block {floor} (see node_status); query a node that keeps full history."));
                    }

                    clampedFrom = start;
                    start = (ulong)floor;
                }
            }

            (ulong scanTo, bool indexed) = PlanLogPage(start, end, selective);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                Filter filter = new()
                {
                    FromBlock = new BlockParameter(start),
                    ToBlock = new BlockParameter(scanTo),
                    Address = addresses,
                    Topics = topicFilter
                };

                using ResultWrapper<IEnumerable<FilterLog>> result = eth.eth_getLogs(filter);
                if (result.Result.ResultType != ResultType.Success)
                {
                    if (result.ErrorCode == ErrorCodes.LimitExceeded && scanTo > start)
                    {
                        scanTo = start + (scanTo - start) / 2;
                        continue;
                    }

                    return Task.FromResult(_executor.Failure("get_logs", result));
                }

                // The logs may be produced lazily, so they are enumerated here, while the module is still rented.
                LogPage page = new(result.Data, start, startLogIndex, scanTo, end, pageLimit, pageBytes, filterHash, indexed, clampedFrom, token);
                return Task.FromResult(_executor.Success((Page: page, Tools: this), static (writer, state) => state.Tools.WriteLogPage(writer, state.Page)));
            }
        }, cancellationToken);
    }

    /// <summary>Chooses the last block of a page starting at <paramref name="start"/>, and whether the log index widened it.</summary>
    private (ulong ScanTo, bool Indexed) PlanLogPage(ulong start, ulong end, bool selective)
    {
        ulong plainEnd = Math.Min(end, McpEthHelpers.SaturatingAdd(start, _maxLogBlockRange - 1));

        // Without an address or topic the index cannot narrow the blocks, so the eth module reads every block anyway.
        if (selective
            && logIndexStorage.Enabled
            && logIndexStorage.MinBlockNumber is { } indexFrom and >= 0
            && logIndexStorage.MaxBlockNumber is { } indexTo and >= 0
            && start >= (ulong)indexFrom
            && start <= (ulong)indexTo)
        {
            ulong indexedEnd = Math.Min(Math.Min(end, (ulong)indexTo), McpEthHelpers.SaturatingAdd(start, _maxIndexedLogBlockRange - 1));
            if (indexedEnd > plainEnd)
            {
                return (indexedEnd, true);
            }
        }

        return (plainEnd, false);
    }

    private CallToolResult? WriteLogPage(Utf8JsonWriter writer, LogPage page)
    {
        long budget = page.ByteBudget;
        int emitted = 0;
        int enumerated = 0;
        long bytes = 0;
        bool hasNext = false;
        ulong nextBlock = 0;
        ulong nextLogIndex = 0;
        FilterLog? last = null;

        writer.WriteStartObject();
        writer.WritePropertyName("logs"u8);
        writer.WriteStartArray();
        foreach (FilterLog log in page.Logs)
        {
            if (++enumerated % LogCancellationCheckInterval == 0)
            {
                page.Token.ThrowIfCancellationRequested();
            }

            if (log.BlockNumber == page.Start && (ulong)log.LogIndex < page.StartLogIndex)
            {
                continue;
            }

            byte[]? json = null;
            if (emitted < page.Limit)
            {
                json = JsonSerializer.SerializeToUtf8Bytes(log, log.GetType(), EthereumJsonSerializer.JsonOptions);
            }

            // Stop at the first log that does not fit, so the next page starts exactly there.
            if (json is null || (emitted > 0 && bytes + json.Length > budget))
            {
                (hasNext, nextBlock, nextLogIndex) = (true, log.BlockNumber, (ulong)log.LogIndex);
                break;
            }

            writer.WriteRawValue(json, skipInputValidation: true);
            bytes += json.Length;
            emitted++;
            last = log;
        }

        writer.WriteEndArray();

        if (!hasNext && rpcConfig.EnableLogsStreamMode && rpcConfig.MaxLogsPerResponse > 0 && enumerated >= rpcConfig.MaxLogsPerResponse)
        {
            if (last is null)
            {
                return McpToolExecutor.Error(McpToolErrorCodes.ResourceExhausted,
                    $"Block {page.Start} has more matching logs than the node's JsonRpc.MaxLogsPerResponse ({rpcConfig.MaxLogsPerResponse}); add address/topic filters.");
            }

            (hasNext, nextBlock, nextLogIndex) = (true, last.BlockNumber, (ulong)last.LogIndex + 1);
        }

        if (!hasNext && page.ScanTo < page.End)
        {
            (hasNext, nextBlock, nextLogIndex) = (true, page.ScanTo + 1, 0);
        }

        // A page that stopped inside a block reports that block as covered (partially); the cursor resumes inside it.
        ulong coveredTo = !hasNext ? page.ScanTo : nextLogIndex == 0 && nextBlock > page.Start ? nextBlock - 1 : nextBlock;
        writer.WritePropertyName("fromBlock"u8);
        McpToolExecutor.WriteValue(writer, page.Start);
        writer.WritePropertyName("toBlock"u8);
        McpToolExecutor.WriteValue(writer, coveredTo);
        writer.WriteBoolean("truncated"u8, hasNext);
        if (hasNext)
        {
            Hash256 nextHash = blockFinder.FindHeader(nextBlock, BlockTreeLookupOptions.RequireCanonical)?.Hash ?? Keccak.Zero;
            writer.WriteString("nextCursor"u8, new McpLogCursor(nextBlock, nextLogIndex, page.End, page.FilterHash, nextHash.BytesToArray()).Encode());
        }

        writer.WriteBoolean("indexed"u8, page.Indexed);
        if (page.ClampedFrom is { } clampedFrom)
        {
            writer.WritePropertyName("clampedFromBlock"u8);
            McpToolExecutor.WriteValue(writer, clampedFrom);
            writer.WriteString("note"u8, $"Blocks {clampedFrom}..{page.Start - 1} are older than this node's receipt history and were skipped; this page starts at block {page.Start}.");
        }
        writer.WriteEndObject();
        return null;
    }

    // The page's logs stay well below MaxResultSize, leaving room for the envelope and the cursor.
    private long MaxLogPageBytes => Math.Max(1, _maxResultSize / 4 * 3);

    private static bool HasTopic(Hash256[]?[]? topics)
    {
        if (topics is null)
        {
            return false;
        }

        foreach (Hash256[]? position in topics)
        {
            if (position is { Length: > 0 })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One <c>eth_getLogs</c> result and the page it is cut into.</summary>
    private readonly record struct LogPage(
        IEnumerable<FilterLog> Logs,
        ulong Start,
        ulong StartLogIndex,
        ulong ScanTo,
        ulong End,
        int Limit,
        long ByteBudget,
        byte[] FilterHash,
        bool Indexed,
        ulong? ClampedFrom,
        CancellationToken Token);
}
