// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Filters;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Mcp.Plugin.Tools.Abi;

namespace Nethermind.Mcp.Plugin.Tools;

internal sealed partial class McpEthTools
{
    private const int DefaultActivityLimit = 20;
    private const int MaxActivityLimit = 50;
    private static readonly Hash256 TransferSingleTopic = Keccak.Compute("TransferSingle(address,address,address,uint256,uint256)");
    private static readonly Hash256 TransferBatchTopic = Keccak.Compute("TransferBatch(address,address,address,uint256[],uint256[])");
    private static readonly Hash256 DepositTopic = Keccak.Compute("Deposit(address,uint256)");
    private static readonly Hash256 WithdrawalTopic = Keccak.Compute("Withdrawal(address,uint256)");

    /// <summary>Returns recent token transfers involving an address, paged in the requested order.</summary>
    [McpServerTool(Name = "address_activity", Title = "Address activity", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpToolOutputSchema("""
        {"type":"object","required":["result"],"properties":{"result":{"type":"object",
          "required":["address","fromBlock","toBlock","movements","pageNetFlows","pageTransactions","truncated","nativeTransfersIncluded","note","order","scannedFrom","scannedTo","coveredTo","indexed","undecodableLogs"],
          "properties":{"address":{"type":"string"},"fromBlock":{"type":["integer","null"],"description":"Lower bound covered in this call: unread cursor position for asc, coveredTo for desc; null before progress."},"toBlock":{"type":["integer","null"],"description":"Upper bound covered in this call: coveredTo for asc, unread cursor position for desc; null before progress."},"scannedFrom":{"type":"integer"},"scannedTo":{"type":"integer"},
            "coveredTo":{"type":["integer","null"],"description":"Last covered block in the requested direction, possibly partial; null before merged progress."},"order":{"type":"string","enum":["asc","desc"]},"indexed":{"type":"boolean","description":"Every attempted window lies inside the log index coverage."},
            "truncationReason":{"type":"string","enum":["limit","byte_budget","time_budget","scan_error"]},
            "movements":{"type":"array","items":{"type":"object",
              "required":["token","standard","from","to","amount","direction","counterparty","transactionHash","blockNumber","timestamp","timestampIso"],
              "properties":{"token":{"type":"string"},"symbol":{"type":"string"},"standard":{"type":"string"},
                "from":{"type":"string"},"to":{"type":"string"},"amount":{"type":"string"},"amountFormatted":{"type":"string"},
                "tokenId":{"type":"string"},"direction":{"type":"string","enum":["in","out","self"]},
                "counterparty":{"type":"string"},"transactionHash":{"type":"string"},"blockNumber":{"type":"integer"},
                "timestamp":{"type":"integer"},"timestampIso":{"type":["string","null"]},
                "valueUsd":{"type":"string"},"priceUpdatedAt":{"type":"integer"},"pricedVia":{"type":"string"}}}},
            "pageNetFlows":{"description":"Fungible net changes for returned movements only; NFTs are excluded.","type":"array","items":{"type":"object","required":["token","change"],
              "properties":{"token":{"type":"string"},"symbol":{"type":"string"},"change":{"type":"string"},
                "changeFormatted":{"type":"string"},"valueUsd":{"type":"string"},"priceUpdatedAt":{"type":"integer"},"pricedVia":{"type":"string"}}}},
            "pageTransactions":{"description":"Distinct transactions in the returned movements only.","type":"integer"},
            "undecodableLogs":{"type":"integer","description":"Matching transfer logs with non-standard layouts or invalid data/topics; their effects are unknown."},
            "truncated":{"type":"boolean"},"nextCursor":{"type":"string"},"clampedFromBlock":{"type":"integer"},
            "nativeTransfersIncluded":{"type":"boolean"},"note":{"type":"string"},"tokenMetadataOmitted":{"type":"integer","description":"Returned token/block snapshots skipped by metadata count/time limits; cache hits are served after the deadline."},
            "tokenMetadataUnavailable":{"type":"integer","description":"Returned token/block snapshots whose metadata could not be read; distinct from budget omissions and proven metadata changes."},
            "batchMovementsOmitted":{"type":"integer"},"usdNotes":{"type":"array","items":{"type":"string"}}}}}}
        """)]
    [Description("Pages ERC-20, ERC-721 and ERC-1155 Transfer logs plus wrapped-native Deposit/Withdrawal logs involving an address. " +
        "Merges incoming and outgoing event searches, newest first by default (order=desc; asc is also supported), de-duplicates self transfers, and reports direction, counterparty, " +
        "formatted token amounts, transaction hashes and timestamps. pageNetFlows sums only returned fungible movements; pageTransactions counts transactions of all returned movements. " +
        "NFT amounts are not aggregated. undecodableLogs counts matching transfer events with non-standard layouts or invalid data/topics. Optional token restricts the contract. " +
        "Native ETH/xDAI value transfers are not included because they emit no logs; use explain_transaction for one transaction. " +
        "scannedFrom/scannedTo give the attempted scan bounds; fromBlock/toBlock give the covered span. coveredTo is the last covered block in the requested order (possibly partial); all coverage fields are null before progress. " +
        "Later scan errors preserve earlier movements with a note and retry cursor. Use nextCursor until truncated is false; a cursor binds the original filter, order and selector types and expires on restart or reorg.")]
    public Task<CallToolResult> AddressActivity(
        [Description(AddressDescription)] string address,
        [Description("First block, as a number or block tag. Omit for the most recent MaxLogBlockRange blocks (or MaxIndexedLogBlockRange when indexed).")] string? fromBlock = null,
        [Description("Last block, as a number or block tag; default latest.")] string toBlock = "latest",
        [Description("Optional token contract address to restrict the events.")] string? token = null,
        [Description("Maximum matching event logs per page, 1 to 50; default 20.")] int limit = DefaultActivityLimit,
        [Description("Opaque nextCursor from a prior page with the same address, token, block selectors and order.")] string? cursor = null,
        [Description("When true, price returned fungible movements at the current quote from verified on-chain Chainlink feeds, including old movements.")] bool includeUsd = false,
        [Description("Movement JSON byte budget, at least 1024 and at most the node limit. Default 131072; pages stop at event boundaries. Increase it if one batch cannot fit.")] int? maxBytes = null,
        [Description("Block/log order: desc (newest first, default), or asc. Descending queries start with a small recent window and widen backward through empty windows within the time budget.")] string order = "desc",
        CancellationToken cancellationToken = default)
    {
        if (!McpToolInput.TryParseAddress(address, nameof(address), out Address? owner, out string? error)
            || !McpToolInput.TryParseBlock(fromBlock ?? "latest", nameof(fromBlock), out BlockParameter? from, out error)
            || !McpToolInput.TryParseBlock(toBlock, nameof(toBlock), out BlockParameter? to, out error))
            return McpEthHelpers.InvalidInput(error);
        Address? tokenAddress = null;
        if (token is not null && !McpToolInput.TryParseAddress(token, nameof(token), out tokenAddress, out error))
            return McpEthHelpers.InvalidInput(error);
        if (limit is < 1 or > MaxActivityLimit)
            return McpEthHelpers.InvalidInput($"'limit' must be between 1 and {MaxActivityLimit}.");

        if (order is not ("asc" or "desc")) return McpEthHelpers.InvalidInput("'order' must be 'asc' or 'desc'.");
        long byteBudget = maxBytes ?? Math.Min(DefaultLogPageBytes, MaxLogPageBytes);
        if (byteBudget < Math.Min(MinLogPageBytes, MaxLogPageBytes) || byteBudget > MaxLogPageBytes)
            return McpEthHelpers.InvalidInput($"'maxBytes' must be between {Math.Min(MinLogPageBytes, MaxLogPageBytes)} and {MaxLogPageBytes}.");

        McpActivityCursor? resume = null;
        McpLogCursor signature = default;
        if (cursor is not null)
        {
            if (!McpActivityCursor.TryDecode(cursor, out resume, out signature))
                return McpEthHelpers.InvalidInput("'cursor' is not a valid address_activity cursor; repeat the query without cursor.");
        }

        return _executor.ExecuteAsync("address_activity", nameof(IEthRpcModule.eth_getLogs), async (eth, cancellation) =>
        {
            Stopwatch clock = Stopwatch.StartNew();
            bool descending = order == "desc";
            bool Stop() => clock.Elapsed.TotalMilliseconds >= config.ToolTimeout * 0.6;
            List<ActivityFilter> filters = ActivityFilters(owner, tokenAddress, chainProfile.WrappedNativeToken);
            ulong? clamped = null;
            ulong? receiptFloor = null;
            string? clampNote = null;
            McpActivityCursor state;
            if (resume is not null)
            {
                if (!signature.IsAt(blockFinder.FindHeader(resume.To, BlockTreeLookupOptions.RequireCanonical)?.Hash))
                    return McpToolExecutor.Error(McpToolErrorCodes.InvalidInput, "The chain reorganised since this activity cursor was issued; restart without cursor.");
                ulong requestedFrom = resume.RequestedFrom;
                ulong requestedTo = resume.To;
                if (fromBlock is not null && from.Type is BlockParameterType.BlockNumber or BlockParameterType.BlockHash
                    && !McpEthHelpers.TryResolveBlockNumber(blockFinder, from, nameof(fromBlock), true, out requestedFrom, out CallToolResult? failure)) return failure;
                if (to.Type is BlockParameterType.BlockNumber or BlockParameterType.BlockHash
                    && !McpEthHelpers.TryResolveBlockNumber(blockFinder, to, nameof(toBlock), true, out requestedTo, out CallToolResult? toFailure)) return toFailure;
                if (!resume.FilterHash.AsSpan().SequenceEqual(ActivityFilterHash(owner, tokenAddress, requestedFrom, requestedTo, order, fromBlock is null ? "default" : from.Type.ToString(), to.Type.ToString())))
                    return McpToolExecutor.Error(McpToolErrorCodes.InvalidInput,
                        "'cursor' belongs to a different address_activity query; repeat the original address, token, block numbers and order, or omit cursor.");
                state = resume;
            }
            else
            {
                if (!McpEthHelpers.TryResolveBlockNumber(blockFinder, to, nameof(toBlock), true, out ulong end, out CallToolResult? failure))
                    return failure;
                ulong start;
                bool indexAtEnd = logIndexStorage.Enabled && logIndexStorage.MinBlockNumber is { } min && min >= 0
                    && logIndexStorage.MaxBlockNumber is { } max && max >= 0 && end >= (ulong)min && end <= McpEthHelpers.SaturatingAdd((ulong)max, 64);
                ulong maxWindow = indexAtEnd ? _maxIndexedLogBlockRange : _maxLogBlockRange;
                if (fromBlock is null) start = end >= maxWindow - 1 ? end - maxWindow + 1 : 0;
                else if (!McpEthHelpers.TryResolveBlockNumber(blockFinder, from, nameof(fromBlock), true, out start, out failure)) return failure;
                if (start > end) return McpToolExecutor.Error(McpToolErrorCodes.InvalidInput, $"fromBlock ({start}) is greater than toBlock ({end}).");
                if (blockFinder.Head?.Number is not { } head)
                    return McpToolExecutor.Error(McpToolErrorCodes.Unavailable, "The node has no head block yet.");
                if (end > head) return McpToolExecutor.Error(McpToolErrorCodes.InvalidInput, $"toBlock {end} exceeds the current head {head}.");
                ulong requestedFrom = start;
                if (capabilities.GetAvailability().OldestReceiptBlock is { } floor && floor > 1 && start < (ulong)floor)
                {
                    if (end < (ulong)floor) return McpToolExecutor.Error(McpToolErrorCodes.Unavailable,
                        $"Blocks {start}..{end} precede this node's receipt history, which starts at {floor}; use a full-history node.");
                    clamped = start;
                    receiptFloor = (ulong)floor;
                    clampNote = fromBlock is null ? $"History before block {floor} is not stored on this node. "
                        : $"Blocks {start}..{floor - 1} are older than this node's receipt history and were skipped. ";
                    start = (ulong)floor;
                }
                if (fromBlock is null && indexAtEnd && start < (ulong)logIndexStorage.MinBlockNumber!.Value)
                    start = (ulong)logIndexStorage.MinBlockNumber.Value;
                state = new McpActivityCursor { From = start, RequestedFrom = requestedFrom, To = end, Window = Math.Min(1000, _maxLogBlockRange),
                    FilterHash = ActivityFilterHash(owner, tokenAddress, requestedFrom, end, order, fromBlock is null ? "default" : from.Type.ToString(), to.Type.ToString()),
                    ReceiptFloor = receiptFloor };
                PlanActivityWindow(state, descending, descending ? end : start, filters.Count);
            }

            if (state.ReceiptFloor is { } retainedFrom)
            {
                clamped = state.RequestedFrom;
                clampNote = fromBlock is null ? $"History before block {retainedFrom} is not stored on this node. "
                    : $"Blocks {state.RequestedFrom}..{retainedFrom - 1} are older than this node's receipt history and were skipped. ";
            }

            ulong frontier = descending
                ? state.Positions.Where(static p => !p.Done).Select(static p => p.Block).DefaultIfEmpty(state.ScanTo).Max()
                : state.Positions.Where(static p => !p.Done).Select(static p => p.Block).DefaultIfEmpty(state.ScanFrom).Min();
            ulong startBlock = descending ? state.ScanFrom : frontier;
            ulong scanTo = descending ? frontier : state.ScanTo;
            ulong? coveredTo = null;
            bool indexed = true;
            bool complete = false;
            CallToolResult? scanFailure = null;
            ActivityStream[]? streams = null;
            Dictionary<ulong, List<FilterLog>> blockLogs = [];
            List<FilterLog> page = [];
            List<(McpActivityCursor State, ulong? CoveredTo)> checkpoints = [];
            try
            {
                while (page.Count < limit && !Stop())
                {
                    startBlock = Math.Min(startBlock, state.ScanFrom);
                    scanTo = Math.Max(scanTo, state.ScanTo);
                    bool windowIndexed = logIndexStorage.Enabled && logIndexStorage.MinBlockNumber is { } indexMin and >= 0
                        && logIndexStorage.MaxBlockNumber is { } indexMax and >= 0
                        && state.ScanFrom >= (ulong)indexMin && state.ScanTo <= (ulong)indexMax;
                    indexed &= windowIndexed;
                    if (!windowIndexed && (state.CheckedThrough is null || state.CheckedThrough < state.ScanTo))
                    {
                        scanFailure = FindMissingReceipts(state.CheckedThrough is { } checkedTo ? checkedTo + 1 : state.ScanFrom, state.ScanTo,
                            bloom => filters.Any(filter => filter.LogFilter.Matches(bloom)), cancellation, Stop,
                            number => state.CheckedThrough = number);
                        if (scanFailure is not null) break;
                    }

                    int previousCount = page.Count;
                    streams = filters.Select((filter, i) => new ActivityStream(filter, state.Positions[i])).ToArray();
                    while (page.Count < limit)
                    {
                        foreach (ActivityStream stream in streams)
                        {
                            while (!stream.Position.Ready && !stream.Position.Done && !Stop())
                            {
                                scanFailure = FillActivityStream(eth, stream, state, filters, tokenAddress, descending, limit, blockLogs, Stop, cancellation);
                                if (scanFailure is not null) break;
                            }
                            if (scanFailure is not null) break;
                        }
                        if (scanFailure is not null || streams.Any(static stream => !stream.Position.Ready && !stream.Position.Done)) break;
                        ActivityStream? first = null;
                        foreach (ActivityStream stream in streams)
                            if (!stream.Position.Done && (first is null || CompareActivity(stream.Position, first.Position, descending) < 0)) first = stream;
                        if (first is null) break;
                        // A cursor retains the known next log's position, not its unbounded data. Only rehydrate it once the merge can emit it.
                        if (first.Logs.Count == 0)
                        {
                            scanFailure = FillActivityStream(eth, first, state, filters, tokenAddress, descending, limit, blockLogs, Stop, cancellation);
                            if (scanFailure is not null) break;
                            if (first.Logs.Count == 0) continue;
                        }
                        FilterLog next = first.Logs.Peek();
                        checkpoints.Add((state.Snapshot(streams.Select(static stream => stream.Position).ToArray()), coveredTo));
                        page.Add(next);
                        coveredTo = next.BlockNumber;
                        foreach (ActivityStream stream in streams)
                        {
                            if (!stream.Position.Done && stream.Position.Block == next.BlockNumber && stream.Position.Index == next.LogIndex)
                            {
                                if (stream.Logs.Count > 0)
                                {
                                    stream.Logs.Dequeue();
                                    stream.Position = stream.Logs.TryPeek(out FilterLog? unread)
                                        ? new ActivityPosition(unread.BlockNumber, unread.LogIndex, stream.Position.Span, Exact: true, Ready: true) : stream.After;
                                }
                                else stream.Position = stream.Position with { Index = next.LogIndex + (descending ? -1 : 1), Ready = false };
                            }
                        }
                    }
                    state.Positions = streams.Select(static stream => stream.Position).ToArray();
                    streams = null;
                    if (scanFailure is not null || !state.Positions.All(static position => position.Done)) break;
                    coveredTo = descending ? state.ScanFrom : state.ScanTo;
                    if (descending ? state.ScanFrom == state.From : state.ScanTo == state.To)
                    {
                        complete = true;
                        break;
                    }
                    if (page.Count == previousCount) state.Window = Math.Min(_maxIndexedLogBlockRange, McpEthHelpers.SaturatingAdd(state.Window, state.Window));
                    PlanActivityWindow(state, descending, descending ? state.ScanFrom - 1 : state.ScanTo + 1, filters.Count);
                    blockLogs.Clear();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { scanFailure = _executor.MapException("address_activity", exception); }
            if (streams is not null) state.Positions = streams.Select(static stream => stream.Position).ToArray();
            if (scanFailure is not null && page.Count == 0 && coveredTo is null) return scanFailure;
            JsonArray movementsJson = [];
            List<(McpTokenMovement Movement, FilterLog Log)> movements = [];
            Dictionary<FilterLog, McpTokenTally> tallies = [];
            foreach (FilterLog log in page)
            {
                List<McpTokenMovement> decoded = [];
                McpTokenTally logTally = new();
                tallies[log] = logTally;
                McpTxTokens.Extract(new LogEntry(log.Address, log.Data, log.Topics), decoded, chainProfile.WrappedNativeToken, logTally);
                foreach (McpTokenMovement movement in decoded)
                    if (movement.From == owner || movement.To == owner) movements.Add((movement, log));
            }

            McpDetachedEth detached = new(async () =>
            {
                ModuleLease<IEthRpcModule> lease = await _executor.RentAsync<IEthRpcModule>(nameof(IEthRpcModule.eth_call));
                return (lease.Module, lease);
            }, _executor.TrackDetached);
            Dictionary<(AddressAsKey Token, Hash256 BlockHash), McpTokenInfo> metadata = [];
            HashSet<(AddressAsKey Token, Hash256 BlockHash)> skippedMetadata = [];
            int metadataOmitted = 0;
            int metadataBudget = 20;
            foreach (IGrouping<Hash256, (McpTokenMovement Movement, FilterLog Log)> blockGroup in movements.GroupBy(static item => item.Log.BlockHash))
            {
                List<Address> tokensAtBlock = [.. blockGroup.Select(static item => item.Movement.Token).Distinct().Take(metadataBudget)];
                foreach (Address omitted in blockGroup.Select(static item => item.Movement.Token).Distinct().Skip(tokensAtBlock.Count))
                {
                    skippedMetadata.Add((omitted, blockGroup.Key));
                    metadataOmitted++;
                }
                HashSet<AddressAsKey> skippedAtBlock = [];
                (Dictionary<AddressAsKey, McpTokenInfo> found, int skipped) = McpTxTokens.LookUp(
                    tokenMetadata, eth, tokensAtBlock, new BlockParameter(blockGroup.Key), tokensAtBlock.Count, cancellation,
                    () => clock.ElapsedMilliseconds > config.ToolTimeout * 0.7, detached, skippedAtBlock);
                foreach ((AddressAsKey tokenKey, McpTokenInfo info) in found) metadata[(tokenKey, blockGroup.Key)] = info;
                foreach (AddressAsKey tokenKey in skippedAtBlock) skippedMetadata.Add((tokenKey, blockGroup.Key));
                metadataOmitted += skipped;
                metadataBudget -= tokensAtBlock.Count;
            }
            Dictionary<AddressAsKey, BigInteger> net = [];
            Dictionary<AddressAsKey, McpPriceQuote?> quotes = [];
            HashSet<string> usdNotes = [];
            HashSet<Hash256> hashes = [];
            foreach ((McpTokenMovement movement, FilterLog log) in movements)
            {
                metadata.TryGetValue((movement.Token, log.BlockHash), out McpTokenInfo? info);
                JsonObject item = McpTxTokens.MovementJson(movement, info);
                bool self = movement.From == owner && movement.To == owner;
                item["direction"] = self ? "self" : movement.To == owner ? "in" : "out";
                item["counterparty"] = McpEthHelpers.Checksum(movement.Standard == "WETH" ? movement.Token : movement.To == owner ? movement.From : movement.To);
                item["transactionHash"] = log.TransactionHash.ToString();
                item["blockNumber"] = (long)log.BlockNumber;
                item["timestamp"] = (long)log.BlockTimestamp;
                item["timestampIso"] = McpEthHelpers.ToIso(log.BlockTimestamp);
                movementsJson.Add(item);
            }

            long bytes = 2;
            int acceptedRows = 0;
            int acceptedLogs = 0;
            bool byteCut = false;
            McpTokenTally tally = new();
            foreach (FilterLog log in page)
            {
                int endRow = acceptedRows;
                long groupBytes = 0;
                while (endRow < movements.Count && ReferenceEquals(movements[endRow].Log, log))
                {
                    groupBytes += JsonSerializer.SerializeToUtf8Bytes(movementsJson[endRow]).Length + (endRow == 0 ? 0 : 1);
                    endRow++;
                }
                if (bytes + groupBytes > byteBudget)
                {
                    if (acceptedLogs == 0) return McpToolExecutor.Error(McpToolErrorCodes.ResourceExhausted,
                        $"One event's movements need {groupBytes + 2} bytes; increase maxBytes to at least that amount (node maximum {MaxLogPageBytes}), or restrict the token filter.");
                    (state, coveredTo) = checkpoints[acceptedLogs];
                    complete = false;
                    byteCut = true;
                    break;
                }
                bytes += groupBytes;
                acceptedRows = endRow;
                acceptedLogs++;
                tally.Omitted += tallies[log].Omitted;
                tally.Undecodable += tallies[log].Undecodable;
            }
            if (byteCut)
            {
                page.RemoveRange(acceptedLogs, page.Count - acceptedLogs);
                movements.RemoveRange(acceptedRows, movements.Count - acceptedRows);
                while (movementsJson.Count > acceptedRows) movementsJson.RemoveAt(movementsJson.Count - 1);
            }
            Dictionary<AddressAsKey, McpTokenInfo> flowMetadata = [];
            HashSet<AddressAsKey> changedMetadata = [];
            HashSet<(AddressAsKey Token, Hash256 BlockHash)> unavailableMetadata = [];
            foreach (IGrouping<AddressAsKey, (McpTokenMovement Movement, FilterLog Log)> tokenGroup in movements.GroupBy(static item => (AddressAsKey)item.Movement.Token))
            {
                List<McpTokenInfo?> snapshots = [];
                foreach ((AddressAsKey Token, Hash256 BlockHash) key in tokenGroup
                    .Select(static item => (Token: (AddressAsKey)item.Movement.Token, item.Log.BlockHash)).Distinct())
                {
                    metadata.TryGetValue(key, out McpTokenInfo? info);
                    snapshots.Add(info);
                    if (info is null && !skippedMetadata.Contains(key)) unavailableMetadata.Add(key);
                }
                ClassifyFlowMetadata(snapshots, out McpTokenInfo? consistent, out bool changed, out bool unavailable);
                if (!unavailable && consistent is not null) flowMetadata[tokenGroup.Key] = consistent;
                if (changed) changedMetadata.Add(tokenGroup.Key);
            }
            if (includeUsd)
            {
                foreach ((McpTokenMovement movement, FilterLog log) in movements.Where(static item => item.Movement.IsFungible))
                    if (chainProfile.PriceFeed(movement.Token.ToString()) is not null
                        && (!metadata.TryGetValue((movement.Token, log.BlockHash), out McpTokenInfo? info) || info.Decimals is null))
                        usdNotes.Add($"USD omitted for {McpEthHelpers.Checksum(movement.Token)} at block {log.BlockNumber}: "
                            + (skippedMetadata.Contains((movement.Token, log.BlockHash))
                                ? "metadata not read within the time budget or lookup limit." : McpTokenMetadata.MissingDecimalsNote(info)));
                Dictionary<string, McpPriceResult> prices = await priceReader.ReadOptionalBatchAsync(_executor,
                    movements.Where(item => item.Movement.IsFungible
                            && metadata.TryGetValue((item.Movement.Token, item.Log.BlockHash), out McpTokenInfo? info) && info.Decimals is not null)
                        .Select(static item => item.Movement.Token.ToString()), BlockParameter.Latest,
                    McpPriceReader.OptionalBudget(config.ToolTimeout, clock, _executor.RemainingTime), cancellation);
                foreach ((string asset, McpPriceResult price) in prices)
                {
                    quotes[new Address(asset)] = price.Quote;
                    if (price.Quote is not { Stale: false }) usdNotes.Add(price.Reason);
                }
                int row = 0;
                foreach (FilterLog log in page)
                {
                    int firstRow = row;
                    long extraBytes = 0;
                    while (row < movements.Count && ReferenceEquals(movements[row].Log, log))
                    {
                        McpTokenMovement movement = movements[row].Movement;
                        if (movement.IsFungible && metadata.TryGetValue((movement.Token, log.BlockHash), out McpTokenInfo? info) && info.Decimals is { } decimals
                            && quotes.TryGetValue(movement.Token, out McpPriceQuote? quote) && quote is { Stale: false })
                        {
                            JsonObject item = movementsJson[row]!.AsObject();
                            int previousBytes = JsonSerializer.SerializeToUtf8Bytes(item).Length;
                            item["valueUsd"] = quote.ValueUsd(movement.Amount, decimals);
                            item["priceUpdatedAt"] = (long)quote.UpdatedAt;
                            item["pricedVia"] = quote.PricedVia;
                            extraBytes += JsonSerializer.SerializeToUtf8Bytes(item).Length - previousBytes;
                        }
                        row++;
                    }
                    if (bytes + extraBytes > byteBudget)
                    {
                        for (int i = firstRow; i < row; i++)
                        {
                            JsonObject item = movementsJson[i]!.AsObject();
                            item.Remove("valueUsd");
                            item.Remove("priceUpdatedAt");
                            item.Remove("pricedVia");
                        }
                        usdNotes.Add("Movement USD fields omitted because they exceed the movement byte budget; increase maxBytes to include them.");
                    }
                    else bytes += extraBytes;
                }
            }
            foreach ((McpTokenMovement movement, FilterLog log) in movements)
            {
                hashes.Add(log.TransactionHash);
                if (!movement.IsFungible) continue;
                bool self = movement.From == owner && movement.To == owner;
                BigInteger delta = self ? BigInteger.Zero : movement.To == owner ? (BigInteger)movement.Amount : -(BigInteger)movement.Amount;
                net[movement.Token] = net.TryGetValue(movement.Token, out BigInteger previous) ? previous + delta : delta;
            }

            JsonArray flows = [];
            foreach ((AddressAsKey tokenKey, BigInteger change) in net)
            {
                flowMetadata.TryGetValue(tokenKey, out McpTokenInfo? info);
                JsonObject flow = new() { ["token"] = McpEthHelpers.Checksum(tokenKey), ["change"] = change.ToString() };
                if (info?.Symbol is not null) flow["symbol"] = info.Symbol;
                if (info?.Decimals is { } decimals) flow["changeFormatted"] = McpTokenMetadata.FormatUnits(change, decimals);
                if (includeUsd && info?.Decimals is { } flowDecimals && quotes.TryGetValue(tokenKey, out McpPriceQuote? quote)
                    && quote?.Stale == false)
                {
                    string value = quote.ValueUsd(BigInteger.Abs(change), flowDecimals);
                    flow["valueUsd"] = change.Sign < 0 && value != "0" ? "-" + value : value;
                    flow["priceUpdatedAt"] = (long)quote.UpdatedAt;
                    flow["pricedVia"] = quote.PricedVia;
                }
                flows.Add(flow);
            }

            bool truncated = !complete;
            string truncationReason = byteCut ? "byte_budget" : page.Count >= limit ? "limit" : scanFailure is not null ? "scan_error" : "time_budget";
            state.HadMatches |= acceptedLogs > 0;
            JsonObject output = new()
            {
                ["address"] = McpEthHelpers.Checksum(owner),
                ["fromBlock"] = coveredTo is null ? null : (long)(descending ? coveredTo.Value : frontier),
                ["toBlock"] = coveredTo is null ? null : (long)(descending ? frontier : coveredTo.Value),
                ["scannedFrom"] = (long)startBlock, ["scannedTo"] = (long)scanTo, ["coveredTo"] = (long?)coveredTo,
                ["order"] = order, ["indexed"] = indexed,
                ["movements"] = movementsJson, ["pageNetFlows"] = flows, ["pageTransactions"] = hashes.Count,
                ["truncated"] = truncated, ["nativeTransfersIncluded"] = false, ["undecodableLogs"] = tally.Undecodable,
                ["note"] = "Native ETH/xDAI value transfers are not included because plain sends emit no logs; use explain_transaction for a transaction."
            };
            if (scanFailure is not null)
                output["note"] = output["note"]!.GetValue<string>() + $" Scan stopped at block {(descending ? state.ScanTo : state.ScanFrom)} ({McpToolExecutor.ReadError(scanFailure)?.GetProperty("code").GetString() ?? McpToolErrorCodes.InternalError}); continue with nextCursor.";
            else if (coveredTo is null)
                output["note"] = output["note"]!.GetValue<string>() + " The scan time budget ended before merged coverage was established; continue with nextCursor.";
            else if (complete && !state.HadMatches)
                output["note"] = output["note"]!.GetValue<string>() + $" No activity in blocks {state.From}..{state.To}.";
            else if (truncated && page.Count == 0)
                output["note"] = output["note"]!.GetValue<string>() + " The scan time budget ended; continue with nextCursor.";
            if (McpTxTokens.UndecodableNote(tally.Undecodable) is { } undecodable) output["note"] = output["note"]!.GetValue<string>() + " " + undecodable;
            if (changedMetadata.Count > 0)
                output["note"] = output["note"]!.GetValue<string>() + " Token metadata changed across returned blocks; pageNetFlows keep raw changes without a symbol or formatted amount for those tokens.";
            if (unavailableMetadata.Count > 0)
            {
                output["tokenMetadataUnavailable"] = unavailableMetadata.Count;
                output["note"] = output["note"]!.GetValue<string>() + " Token metadata was unavailable at one or more returned blocks; affected pageNetFlows keep raw changes.";
            }
            if (metadataOmitted > 0)
            {
                int omittedOnPage = movements.Select(static item => (Token: (AddressAsKey)item.Movement.Token, item.Log.BlockHash)).Distinct()
                    .Count(key => skippedMetadata.Contains(key));
                if (omittedOnPage > 0) output["tokenMetadataOmitted"] = omittedOnPage;
            }
            if (includeUsd && usdNotes.Count > 0)
            {
                JsonArray priceNotes = [];
                foreach (string priceNote in usdNotes) priceNotes.Add(priceNote);
                output["usdNotes"] = priceNotes;
            }
            if (tally.Omitted > 0) output["batchMovementsOmitted"] = tally.Omitted;
            if (clamped is { } floorClamped)
            {
                output["clampedFromBlock"] = (long)floorClamped;
                output["note"] = clampNote + output["note"]!.GetValue<string>();
            }
            if (truncated)
            {
                output["truncationReason"] = truncationReason;
                Hash256 blockHash = blockFinder.FindHeader(state.To, BlockTreeLookupOptions.RequireCanonical)?.Hash ?? Keccak.Zero;
                output["nextCursor"] = state.Encode(blockHash);
            }

            return _executor.Success(output);
        }, cancellationToken);
    }

    internal static void ClassifyFlowMetadata(IEnumerable<McpTokenInfo?> snapshots, out McpTokenInfo? consistent,
        out bool changed, out bool unavailable)
    {
        consistent = null;
        unavailable = false;
        HashSet<(string? Symbol, byte? Decimals)> versions = [];
        foreach (McpTokenInfo? info in snapshots)
        {
            if (info is null)
            {
                unavailable = true;
                continue;
            }

            consistent ??= info;
            versions.Add((info.Symbol, info.Decimals));
        }
        changed = versions.Count > 1;
        if (changed || unavailable) consistent = null;
    }

    private static byte[] ActivityFilterHash(Address owner, Address? token, ulong from, ulong to, string order, string fromType, string toType)
    {
        byte[] account = new byte[32];
        owner.Bytes.CopyTo(account.AsSpan(12));
        return McpLogCursor.ComputeFilterHash(new BlockParameter(from), new BlockParameter(to), token is null ? null : [token],
            [[new Hash256(account)], [Keccak.Compute($"address_activity:{order}:{fromType}:{toType}")]]);
    }

    private void PlanActivityWindow(McpActivityCursor state, bool descending, ulong next, int filters)
    {
        if (descending)
        {
            state.ScanTo = next;
            ulong span = state.Window;
            if (!(logIndexStorage.Enabled && logIndexStorage.MinBlockNumber is { } min && min >= 0
                && logIndexStorage.MaxBlockNumber is { } max && max >= 0 && next >= (ulong)min && next <= (ulong)max))
                span = Math.Min(span, _maxLogBlockRange);
            state.ScanFrom = Math.Max(state.From, next >= span - 1 ? next - span + 1 : 0);
            if (logIndexStorage.Enabled && logIndexStorage.MaxBlockNumber is { } indexTo && indexTo >= 0 && next > (ulong)indexTo)
                state.ScanFrom = Math.Max(state.ScanFrom, (ulong)indexTo + 1);
            if (logIndexStorage.Enabled && logIndexStorage.MinBlockNumber is { } indexFrom && indexFrom >= 0 && next >= (ulong)indexFrom)
                state.ScanFrom = Math.Max(state.ScanFrom, (ulong)indexFrom);
        }
        else
        {
            state.ScanFrom = next;
            state.ScanTo = PlanLogPage(next, state.To, true).ScanTo;
        }
        state.CheckedThrough = null;
        state.Positions = Enumerable.Repeat(new ActivityPosition(descending ? state.ScanTo : state.ScanFrom,
            descending ? long.MaxValue : 0, state.ScanTo - state.ScanFrom + 1), filters).ToArray();
    }

    private CallToolResult? FillActivityStream(IEthRpcModule eth, ActivityStream stream, McpActivityCursor state,
        List<ActivityFilter> filters, Address? token, bool descending, int limit, Dictionary<ulong, List<FilterLog>> blockLogs,
        Func<bool> stop, CancellationToken cancellation)
    {
        ActivityPosition position = stream.Position;
        ulong low = descending ? Math.Max(state.ScanFrom, position.Block >= position.Span - 1 ? position.Block - position.Span + 1 : 0) : position.Block;
        ulong high = descending ? position.Block : Math.Min(state.ScanTo, McpEthHelpers.SaturatingAdd(position.Block, position.Span - 1));
        List<FilterLog> logs = [];
        bool block = position.Exact;
        bool cut = false;
        while (!block)
        {
            if (stop()) return null;
            cancellation.ThrowIfCancellationRequested();
            using ResultWrapper<IEnumerable<FilterLog>> result = eth.eth_getLogs(new Filter
            {
                FromBlock = new BlockParameter(low), ToBlock = new BlockParameter(high),
                Address = stream.Filter.Addresses, Topics = stream.Filter.Topics
            });
            if (result.Result.ResultType != ResultType.Success)
            {
                if (result.ErrorCode != ErrorCodes.LimitExceeded) return _executor.Failure("address_activity", result);
                if (low == high) { block = true; stream.Position = position with { Exact = true }; break; }
                if (descending) low += (high - low + 1) / 2;
                else high = low + (high - low) / 2;
                position = position with { Span = high - low + 1 };
                stream.Position = position;
                continue;
            }
            int readLimit = descending ? Math.Max(128, limit) : limit;
            foreach (FilterLog log in result.Data)
            {
                cancellation.ThrowIfCancellationRequested();
                logs.Add(log);
                if (logs.Count > readLimit) { cut = true; break; }
            }
            cut |= StreamLogCap > 0 && logs.Count >= StreamLogCap;
            if (descending && cut)
            {
                // An ascending capped stream cannot prove the last log; narrow toward newer blocks first.
                logs.Clear();
                if (low == high) { block = true; stream.Position = position with { Exact = true }; break; }
                low += (high - low + 1) / 2;
                position = position with { Span = high - low + 1 };
                stream.Position = position;
                cut = false;
                continue;
            }
            break;
        }
        if (block)
        {
            if (stop() && !position.Ready) return null;
            ulong number = descending ? high : low;
            if (!blockLogs.TryGetValue(number, out List<FilterLog>? all))
            {
                all = ActivityBlockLogs(eth, number, filters, token, cancellation, out CallToolResult? failure);
                if (failure is not null) return failure;
                blockLogs[number] = all;
            }
            logs = [];
            foreach (FilterLog log in all)
            {
                cancellation.ThrowIfCancellationRequested();
                if (position.Exact && (descending ? log.LogIndex > position.Index : log.LogIndex < position.Index)) continue;
                if (stream.Filter.LogFilter.Accepts(new LogEntry(log.Address, log.Data, log.Topics))) logs.Add(log);
            }
            low = high = number;
            cut = false;
        }
        logs.Sort((a, b) => CompareActivity(a, b, descending));
        int count = Math.Min(logs.Count, limit + 1);
        stream.After = cut || count < logs.Count
            ? new ActivityPosition(logs[count - 1].BlockNumber, logs[count - 1].LogIndex + (descending ? -1 : 1), position.Span, Exact: true)
            : NextActivityBlock(descending ? low : high, position.Span, state, descending);
        for (int i = 0; i < count; i++) stream.Logs.Enqueue(logs[i]);
        stream.Position = count == 0 ? stream.After : new ActivityPosition(logs[0].BlockNumber, logs[0].LogIndex, position.Span, Exact: true, Ready: true);
        return null;
    }

    private static ActivityPosition NextActivityBlock(ulong block, ulong span, McpActivityCursor state, bool descending) =>
        (descending ? block <= state.ScanFrom : block >= state.ScanTo)
            ? new ActivityPosition(block, 0, span, Done: true)
            : new ActivityPosition(descending ? block - 1 : block + 1, descending ? long.MaxValue : 0,
                Math.Min(state.ScanTo - state.ScanFrom + 1, McpEthHelpers.SaturatingAdd(span, span)));

    private static int CompareActivity(ActivityPosition a, ActivityPosition b, bool descending)
    {
        int result = a.Block != b.Block ? a.Block.CompareTo(b.Block) : a.Index.CompareTo(b.Index);
        return descending ? -result : result;
    }

    private static int CompareActivity(FilterLog a, FilterLog b, bool descending)
    {
        int result = a.BlockNumber != b.BlockNumber ? a.BlockNumber.CompareTo(b.BlockNumber) : a.LogIndex.CompareTo(b.LogIndex);
        return descending ? -result : result;
    }

    private sealed class ActivityStream(ActivityFilter filter, ActivityPosition position)
    {
        public ActivityFilter Filter { get; } = filter;
        public ActivityPosition Position { get; set; } = position;
        public ActivityPosition After { get; set; }
        public Queue<FilterLog> Logs { get; } = new();
    }

    private static List<ActivityFilter> ActivityFilters(Address owner, Address? token, Address? wrapped)
    {
        HashSet<AddressAsKey>? address = token is null ? null : [token];
        byte[] padded = new byte[32];
        owner.Bytes.CopyTo(padded.AsSpan(12));
        Hash256 account = new(padded);
        List<ActivityFilter> filters = [];
        Add(address, [[McpKnownAbi.TransferTopic], [account]]);
        Add(address, [[McpKnownAbi.TransferTopic], null, [account]]);
        Add(address, [[TransferSingleTopic, TransferBatchTopic], null, [account]]);
        Add(address, [[TransferSingleTopic, TransferBatchTopic], null, null, [account]]);
        if (wrapped is not null && (token is null || token == wrapped))
            Add([wrapped], [[DepositTopic, WithdrawalTopic], [account]]);
        return filters;

        void Add(HashSet<AddressAsKey>? addresses, Hash256[]?[] topics) =>
            filters.Add(new ActivityFilter(CreateLogFilter(addresses, topics), addresses, topics));
    }

    private List<FilterLog> ActivityBlockLogs(IEthRpcModule eth, ulong block, List<ActivityFilter> filters,
        Address? token, CancellationToken cancellation, out CallToolResult? failure)
    {
        failure = null;
        if (blockFinder.FindHeader(block, BlockTreeLookupOptions.RequireCanonical) is { } header && capabilities.CheckReceipts(header) is { } missing)
        {
            failure = missing;
            return [];
        }

        using ResultWrapper<IEnumerable<ReceiptForRpc>?> receipts = eth.eth_getBlockReceipts(new BlockParameter(block));
        if (receipts.Result.ResultType != ResultType.Success)
        {
            failure = _executor.Failure("address_activity", receipts);
            return [];
        }

        if (receipts.Data is null)
        {
            failure = McpToolExecutor.Error(McpToolErrorCodes.Unavailable, $"Receipts for block {block} are unavailable.");
            return [];
        }

        List<FilterLog> logs = [];
        foreach (ReceiptForRpc receipt in receipts.Data)
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (LogEntryForRpc log in receipt.Logs ?? [])
            {
                if (token is not null && log.Address != token) continue;
                bool matches = false;
                foreach (ActivityFilter filter in filters) if (filter.LogFilter.Accepts(log.ToLogEntry())) { matches = true; break; }
                if (matches && log.LogIndex is { } index)
                    logs.Add(new FilterLog(index, log.BlockNumber ?? block, log.BlockTimestamp ?? 0, log.BlockHash,
                        (int)(log.TransactionIndex ?? 0), log.TransactionHash, log.Address, log.Data, log.Topics, log.Removed ?? false));
            }
        }

        return logs;
    }

    private readonly record struct ActivityFilter(LogFilter LogFilter, HashSet<AddressAsKey>? Addresses, Hash256[]?[] Topics);
}
