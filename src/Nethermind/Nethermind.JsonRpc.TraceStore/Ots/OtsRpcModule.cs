// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.JsonRpc.TraceStore.Ots;

/// <summary>Serves the account-history OTS subset without requiring historical state.</summary>
/// <remarks>
/// Searches scan stored traces one block at a time outside certified address-index coverage.
/// Sparse searches outside coverage can time out. Missing history never
/// becomes an empty page or a claim that the end of an address's history has been reached.
/// </remarks>
public sealed class OtsRpcModule(
    [KeyFilter(TraceStorePlugin.DbName)] IDb traceStore,
    ITraceSerializer<ParityLikeTxTrace> serializer,
    IBlockFinder blockFinder,
    IReceiptFinder receiptFinder,
    ISpecProvider specProvider,
    ITraceStoreConfig traceStoreConfig,
    IJsonRpcConfig rpcConfig,
    TraceAddressIndex? addressIndex = null) : IOtsRpcModule
{
    private const int MaxPageSize = 25;
    private const string ChainChanged = "Canonical chain changed during OTS search; retry the request.";

    /// <inheritdoc />
    public ResultWrapper<int> ots_getApiLevel() => ResultWrapper<int>.Success(0);

    /// <inheritdoc />
    public ResultWrapper<OtsSearchResult> ots_searchTransactionsBefore(Address address, ulong blockNumber, int pageSize) =>
        Search(address, blockNumber, pageSize, backwards: true);

    /// <inheritdoc />
    public ResultWrapper<OtsSearchResult> ots_searchTransactionsAfter(Address address, ulong blockNumber, int pageSize) =>
        Search(address, blockNumber, pageSize, backwards: false);

    private ResultWrapper<OtsSearchResult> Search(Address address, ulong cursor, int pageSize, bool backwards)
    {
        if (address is null || pageSize is < 1 or > MaxPageSize)
            return ResultWrapper<OtsSearchResult>.Fail($"An address and page size between 1 and {MaxPageSize} are required.", ErrorCodes.InvalidParams);

        if (blockFinder.Head is not { } head)
            return ResultWrapper<OtsSearchResult>.Fail("Chain head is unavailable.", ErrorCodes.ResourceUnavailable);

        using RentedTimeout timeout = rpcConfig.RentTimeout();
        CancellationToken ct = timeout.Token;
        OtsSearchResult result = new();
        bool hasMore = false;
        // Genesis contains no user transactions. Avoid overflowing an after cursor at ulong.MaxValue.
        ulong number = backwards ? (cursor == 0 ? head.Number : Math.Min(cursor - 1, head.Number))
            : cursor >= head.Number ? 0 : cursor + 1;
        Block? previous = null;
        Block? highestScanned = null;

        // Precompile calls can be absent from postings, including in blocks that candidates would skip.
        bool canIndex = !specProvider.GenesisSpec.IsPrecompile(address) && !specProvider.GetSpec(head.Header).IsPrecompile(address);
        if (canIndex && addressIndex is not null)
        {
            foreach (ForkActivation activation in specProvider.TransitionActivations)
                if (specProvider.GetSpec(activation).IsPrecompile(address))
                {
                    canIndex = false;
                    break;
                }
        }
        // Each certified candidate has a match, so pageSize + 1 blocks suffice including lookahead.
        TraceAddressIndex.Candidates? candidates = number == 0 || !canIndex ? null : addressIndex?.FindCandidates(address,
            backwards ? 1 : number, backwards ? number : head.Number, backwards, pageSize + 1, ct);
        int nextCandidate = 0;
        bool crossedCoverage = false;
        while (number > 0 && number <= head.Number)
        {
            ct.ThrowIfCancellationRequested();
            Hash256? indexedHash = null;
            if (candidates is not null && number >= (ulong)candidates.Coverage.From && number <= (ulong)candidates.Coverage.To)
            {
                if (!crossedCoverage && previous is not null && (backwards
                    ? previous.ParentHash != candidates.Coverage.Hash
                    : previous.Hash != candidates.FirstParent))
                    return ResultWrapper<OtsSearchResult>.Fail(ChainChanged, ErrorCodes.ResourceUnavailable);
                crossedCoverage = true;
                if (nextCandidate == candidates.Blocks.Length)
                {
                    number = backwards ? (ulong)candidates.Coverage.From - 1 : (ulong)candidates.Coverage.To + 1;
                    continue;
                }
                number = (ulong)candidates.Blocks[nextCandidate];
                indexedHash = candidates.Hashes[nextCandidate++];
            }
            Block? block = blockFinder.FindBlock(number);
            if (block is null)
                return ResultWrapper<OtsSearchResult>.Fail($"Block {number} is unavailable.", ErrorCodes.ResourceUnavailable);
            if (indexedHash is not null && block.Hash != indexedHash)
                return ResultWrapper<OtsSearchResult>.Fail(ChainChanged, ErrorCodes.ResourceUnavailable);
            if (crossedCoverage && candidates is not null && (backwards
                ? number == (ulong)candidates.Coverage.From - 1 && block.Hash != candidates.FirstParent
                : number == (ulong)candidates.Coverage.To + 1 && block.ParentHash != candidates.Coverage.Hash))
                return ResultWrapper<OtsSearchResult>.Fail(ChainChanged, ErrorCodes.ResourceUnavailable);

            bool adjacent = previous is not null && (backwards ? previous.Number == block.Number + 1 : block.Number == previous.Number + 1);
            if (!blockFinder.IsMainChain(block.Header) || (previous is not null && (adjacent
                ? backwards ? previous.ParentHash != block.Hash : block.ParentHash != previous.Hash
                : !blockFinder.IsMainChain(previous.Header))))
                return ResultWrapper<OtsSearchResult>.Fail(ChainChanged, ErrorCodes.ResourceUnavailable);
            previous = block;
            if (highestScanned is null || block.Number > highestScanned.Number) highestScanned = block;

            if (specProvider.GetSpec(block.Header).IsPrecompile(address))
                return ResultWrapper<OtsSearchResult>.Fail("Stored traces cannot certify complete precompile history.", ErrorCodes.ResourceUnavailable);

            bool indexed = indexedHash is not null;
            if (indexed && block.Transactions.Length == 0)
                return ResultWrapper<OtsSearchResult>.Fail("Trace address index references an empty block; reset the index.", ErrorCodes.ResourceUnavailable);
            if (block.Transactions.Length != 0)
            {
                SearchResult<bool[]> matches;
                if (indexed && addressIndex!.TryMatchPositions(block, address, ct) is { } positions)
                    matches = positions;
                else
                {
                    SearchResult<List<ParityLikeTxTrace>> stored = ReadTraces(block.Header);
                    if (stored.IsError) return ResultWrapper<OtsSearchResult>.Fail(stored);
                    matches = MatchTransactions(block, stored.Object!, address, ct);
                }
                if (matches.IsError) return ResultWrapper<OtsSearchResult>.Fail(matches);

                bool found = Array.IndexOf(matches.Object!, true) >= 0;
                if (!found && indexed)
                    return ResultWrapper<OtsSearchResult>.Fail("Trace address index candidate does not match stored traces; reset the index.", ErrorCodes.ResourceUnavailable);
                if (found)
                {
                    // OTS needs one further matching block to distinguish a full terminal page from a middle page.
                    if (result.Txs.Count >= pageSize)
                    {
                        hasMore = true;
                        break;
                    }

                    string? error = AddMatches(result, block, matches.Object!, backwards, ct);
                    if (error is not null)
                        return ResultWrapper<OtsSearchResult>.Fail(error, ErrorCodes.ResourceUnavailable);
                }
            }
            if (backwards) number--;
            else if (number == head.Number) break;
            else number++;
        }

        ct.ThrowIfCancellationRequested();
        if (!blockFinder.IsMainChain(head.Header) || (highestScanned is not null && !blockFinder.IsMainChain(highestScanned.Header))
            || (candidates is not null && !addressIndex!.IsCanonical(candidates.Coverage)))
            return ResultWrapper<OtsSearchResult>.Fail(ChainChanged, ErrorCodes.ResourceUnavailable);

        Hash256? checkedBlock = null;
        foreach (TransactionForRpc transaction in result.Txs)
        {
            ct.ThrowIfCancellationRequested();
            if (transaction.BlockHash == checkedBlock) continue;
            if (blockFinder.FindBlockHash(transaction.BlockNumber!.Value) != transaction.BlockHash)
                return ResultWrapper<OtsSearchResult>.Fail(ChainChanged, ErrorCodes.ResourceUnavailable);
            checkedBlock = transaction.BlockHash;
        }
        if (!backwards)
        {
            result.Txs.Reverse();
            result.Receipts.Reverse();
        }
        result.FirstPage = backwards ? cursor == 0 || cursor > head.Number : !hasMore;
        result.LastPage = backwards ? !hasMore : cursor == 0;
        return ResultWrapper<OtsSearchResult>.Success(result);
    }

    private static SearchResult<bool[]> MatchTransactions(Block block, List<ParityLikeTxTrace> traces, Address address, CancellationToken ct)
    {
        if (OtsTraceReader.Validate(block, traces, ct) is { } error)
            return new(error, ErrorCodes.ResourceUnavailable);
        bool[] matches = new bool[block.Transactions.Length];
        Stack<ParityTraceAction> actions = new();
        foreach (ParityLikeTxTrace trace in traces)
        {
            ct.ThrowIfCancellationRequested();
            if (trace.TransactionHash is null) continue; // Block rewards are not transactions.
            int position = trace.TransactionPosition!.Value;
            actions.Clear();
            actions.Push(trace.Action!);
            while (actions.TryPop(out ParityTraceAction? action))
            {
                ct.ThrowIfCancellationRequested();
                // Search is call involvement, including zero-value/reverted calls, not committed value transfers.
                if (action.From == address || GetDestination(action, trace.Action!, block.Transactions[position]) == address)
                {
                    matches[position] = true;
                    break;
                }
                foreach (ParityTraceAction child in action.Subtraces) actions.Push(child);
            }
        }

        return new(matches);
    }

    private string? AddMatches(OtsSearchResult result, Block block, bool[] matches, bool backwards, CancellationToken ct)
    {
        TxReceipt[] receipts = receiptFinder.Get(block);
        if (receipts.Length != block.Transactions.Length)
            return $"Receipts for block {block.Number} are unavailable or incomplete.";

        int start = result.Txs.Count;
        int logIndex = 0;
        IReleaseSpec spec = specProvider.GetSpec(block.Header);
        for (int i = 0; i < receipts.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            TxReceipt receipt = receipts[i];
            Transaction tx = block.Transactions[i];
            if (receipt.Index != i || receipt.TxHash != tx.Hash || receipt.BlockHash != block.Hash)
                return $"Receipts for block {block.Number} are inconsistent.";

            if (matches[i])
            {
                tx.SenderAddress ??= receipt.Sender;
                result.Txs.Add(TransactionForRpc.FromTransaction(tx, new(specProvider.ChainId, block.Hash!, block.Number,
                    i, block.Timestamp, block.BaseFeePerGas, receipt)));
                result.Receipts.Add(new(tx.Hash!, receipt, block.Timestamp, tx.GetGasInfo(spec, block.Header), logIndex));
            }
            logIndex += receipt.Logs?.Length ?? 0;
        }

        if (backwards)
        {
            result.Txs.Reverse(start, result.Txs.Count - start);
            result.Receipts.Reverse(start, result.Receipts.Count - start);
        }
        return null;
    }

    /// <inheritdoc />
    public ResultWrapper<List<OtsTraceEntry>> ots_traceTransaction(Hash256 transactionHash)
    {
        if (transactionHash is null)
            return ResultWrapper<List<OtsTraceEntry>>.Fail("A transaction hash is required.", ErrorCodes.InvalidParams);

        using RentedTimeout timeout = rpcConfig.RentTimeout();
        CancellationToken ct = timeout.Token;
        SearchResult<Hash256> blockHash = receiptFinder.SearchForReceiptBlockHash(transactionHash);
        if (blockHash.IsError) return ResultWrapper<List<OtsTraceEntry>>.Fail(blockHash);

        SearchResult<Block> search = blockFinder.SearchForBlock(new BlockParameter(blockHash.Object!, requireCanonical: true));
        if (search.IsError) return ResultWrapper<List<OtsTraceEntry>>.Fail(search);
        Block block = search.Object!;
        SearchResult<List<ParityLikeTxTrace>> stored = ReadTraces(block.Header);
        if (stored.IsError) return ResultWrapper<List<OtsTraceEntry>>.Fail(stored);

        foreach (ParityLikeTxTrace trace in stored.Object!)
        {
            ct.ThrowIfCancellationRequested();
            if (trace.TransactionHash != transactionHash) continue;
            if (trace.Action is null || trace.TransactionPosition is not { } index
                || (uint)index >= (uint)block.Transactions.Length || block.Transactions[index].Hash != transactionHash
                || trace.BlockHash != block.Hash || trace.BlockNumber != block.Number)
                break;

            List<OtsTraceEntry> entries = [];
            Stack<(ParityTraceAction Action, int Depth)> stack = new();
            stack.Push((trace.Action, 0));
            while (stack.TryPop(out (ParityTraceAction Action, int Depth) frame))
            {
                ct.ThrowIfCancellationRequested();
                ParityTraceAction action = frame.Action;
                if (!action.IsPrecompiled)
                {
                    Address? to = GetDestination(action, trace.Action, block.Transactions[index]);
                    // Balance/depth prechecks reject nested creations before a destination exists.
                    if (to is null && frame.Depth > 0 && action.Type == "create" && action.From is not null
                        && action.Result is null && action.Subtraces.Count == 0
                        && action.Error is not null)
                        continue;
                    if (action.From is null || to is null)
                        return ResultWrapper<List<OtsTraceEntry>>.Fail("Stored trace lacks a call's sender or destination.", ErrorCodes.ResourceUnavailable);

                    string type = action.Type switch
                    {
                        "create" => action.CreationMethod == "create2" ? "CREATE2" : "CREATE",
                        "suicide" => "SELFDESTRUCT",
                        "call" => action.CallType?.ToUpperInvariant() ?? "CALL",
                        null when action.CallType == "init" => "CREATE",
                        null when action.CallType == "call" => "CALL",
                        _ => "UNKNOWN"
                    };
                    entries.Add(new()
                    {
                        Type = type,
                        Depth = frame.Depth,
                        From = action.From,
                        To = to,
                        Value = type is "STATICCALL" or "DELEGATECALL" ? null : action.Value,
                        Input = action.Input.ToArray() ?? [],
                        Output = action.Result?.Code ?? action.Result?.Output ?? []
                    });
                }
                for (int i = action.Subtraces.Count - 1; i >= 0; i--)
                    stack.Push((action.Subtraces[i], frame.Depth + 1));
            }

            if (!blockFinder.IsMainChain(block.Header))
                return ResultWrapper<List<OtsTraceEntry>>.Fail(ChainChanged, ErrorCodes.ResourceUnavailable);
            return ResultWrapper<List<OtsTraceEntry>>.Success(entries);
        }
        return ResultWrapper<List<OtsTraceEntry>>.Fail($"Stored trace for {transactionHash} is unavailable.", ErrorCodes.ResourceUnavailable);
    }

    internal static Address? GetDestination(ParityTraceAction action, ParityTraceAction root, Transaction transaction) =>
        action.To ?? action.Result?.Address
        ?? (ReferenceEquals(action, root) && action.CallType == "init" && action.From is not null && !transaction.IsMessageCall
            ? ContractAddress.From(action.From, transaction.Nonce) : null);

    private SearchResult<List<ParityLikeTxTrace>> ReadTraces(BlockHeader header) =>
        OtsTraceReader.Read(traceStore, serializer, traceStoreConfig, header);

}
