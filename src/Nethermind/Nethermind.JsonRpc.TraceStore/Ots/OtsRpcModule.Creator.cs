// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.JsonRpc.TraceStore.Ots;

public sealed partial class OtsRpcModule
{
    /// <inheritdoc />
    public ResultWrapper<OtsContractCreator?> ots_getContractCreator(Address address)
    {
        if (address is null)
            return ResultWrapper<OtsContractCreator?>.Fail("An address is required.", ErrorCodes.InvalidParams);
        if (blockFinder.Head is not { } head)
            return ResultWrapper<OtsContractCreator?>.Fail("Chain head is unavailable.", ErrorCodes.ResourceUnavailable);
        if (!stateReader.HasStateForBlock(head.Header))
            return ResultWrapper<OtsContractCreator?>.Fail($"State for block {head.Number} is unavailable.", ErrorCodes.ResourceUnavailable);

        CancellationTokenSource timeout = rpcConfig.BuildTimeoutCancellationToken();
        try
        {
            if (CheckCreatorAccount(address, head.Header, timeout.Token, out bool hasCreationHistory) is { } error)
                return ResultWrapper<OtsContractCreator?>.Fail(error, ErrorCodes.ResourceUnavailable);
            return hasCreationHistory
                ? FindContractCreator(address, head, timeout.Token)
                : ResultWrapper<OtsContractCreator?>.Success(null);
        }
        finally { JsonRpcConfigExtension.ReturnTimeoutCancellationToken(timeout); }
    }

    private string? CheckCreatorAccount(Address address, BlockHeader head, CancellationToken ct, out bool hasCreationHistory)
    {
        ct.ThrowIfCancellationRequested();
        hasCreationHistory = stateReader.TryGetAccount(head, address, out AccountStruct account) && account.HasCode;
        if (!blockFinder.IsMainChain(head)) return ChainChanged;
        if (!hasCreationHistory) return null;
        if (specProvider.GetSpec(head).IsEip7702Enabled)
        {
            byte[]? code = stateReader.GetCode(account.CodeHash);
            if (code is null) return "Current account code is unavailable.";
            ct.ThrowIfCancellationRequested();
            if (!blockFinder.IsMainChain(head)) return ChainChanged;
            // EIP-7702 delegation is installed by authorization, not by a CREATE transaction.
            if (Eip7702Constants.IsDelegatedCode(code)) hasCreationHistory = false;
        }
        return null;
    }

    private ResultWrapper<OtsContractCreator?> FindContractCreator(Address address, Block head, CancellationToken ct)
    {
        const int candidateBatchSize = 64;
        TraceAddressIndex.Candidates? candidates = addressIndex?.FindCandidates(address, 1, head.Number, true, candidateBatchSize, ct);
        TraceAddressIndex.Checkpoint? coverage = candidates?.Coverage;
        Hash256? firstParent = candidates?.FirstParent;
        int nextCandidate = 0;
        bool crossedCoverage = false;
        Block? previous = null;
        Stack<ParityTraceAction> stack = new();
        for (ulong number = head.Number; number > 0; number--)
        {
            ct.ThrowIfCancellationRequested();
            Hash256? indexedHash = null;
            if (coverage is not null && number >= (ulong)coverage.From && number <= (ulong)coverage.To)
            {
                if (!crossedCoverage && previous is not null && previous.ParentHash != coverage.Hash)
                    return ResultWrapper<OtsContractCreator?>.Fail(ChainChanged, ErrorCodes.ResourceUnavailable);
                crossedCoverage = true;
                if (nextCandidate == candidates!.Blocks.Length && candidates.Blocks.Length == candidateBatchSize)
                {
                    candidates = addressIndex!.FindCandidates(address, 1, number, true, candidateBatchSize, ct);
                    nextCandidate = 0;
                    if (candidates is null || candidates.Coverage != coverage || candidates.FirstParent != firstParent)
                        return ResultWrapper<OtsContractCreator?>.Fail(ChainChanged, ErrorCodes.ResourceUnavailable);
                }
                if (nextCandidate == candidates!.Blocks.Length)
                {
                    number = (ulong)coverage.From - 1;
                    if (number == 0) break;
                }
                else
                {
                    number = (ulong)candidates.Blocks[nextCandidate];
                    indexedHash = candidates.Hashes[nextCandidate++];
                }
            }
            Block? block = blockFinder.FindBlock(number);
            if (block is null)
                return ResultWrapper<OtsContractCreator?>.Fail($"Block {number} is unavailable.", ErrorCodes.ResourceUnavailable);
            if (!blockFinder.IsMainChain(block.Header) || (indexedHash is not null && block.Hash != indexedHash)
                || (crossedCoverage && coverage is not null && number == (ulong)coverage.From - 1 && block.Hash != firstParent)
                || (previous is not null && (previous.Number == number + 1
                    ? previous.ParentHash != block.Hash : !blockFinder.IsMainChain(previous.Header))))
                return ResultWrapper<OtsContractCreator?>.Fail(ChainChanged, ErrorCodes.ResourceUnavailable);
            previous = block;
            if (indexedHash is not null && block.Transactions.Length == 0)
                return ResultWrapper<OtsContractCreator?>.Fail("Trace address index references an empty block; reset the index.", ErrorCodes.ResourceUnavailable);
            if (block.Transactions.Length == 0) continue;

            SearchResult<List<ParityLikeTxTrace>> stored = ReadCreatorTraces(block, address, indexedHash is not null, ct);
            if (stored.IsError) return ResultWrapper<OtsContractCreator?>.Fail(stored);
            SearchResult<OtsContractCreator> creator = FindCreatorInBlock(block, stored.Object!, address, stack, ct);
            if (creator.IsError) return ResultWrapper<OtsContractCreator?>.Fail(creator);
            if (creator.Object is null) continue;
            if (!blockFinder.IsMainChain(head.Header) || !blockFinder.IsMainChain(block.Header)
                || (coverage is not null && !addressIndex!.IsCanonical(coverage)))
                return ResultWrapper<OtsContractCreator?>.Fail(ChainChanged, ErrorCodes.ResourceUnavailable);
            return ResultWrapper<OtsContractCreator?>.Success(creator.Object);
        }
        ct.ThrowIfCancellationRequested();
        return blockFinder.IsMainChain(head.Header) && (previous is null || blockFinder.IsMainChain(previous.Header))
            && (coverage is null || addressIndex!.IsCanonical(coverage))
            ? ResultWrapper<OtsContractCreator?>.Success(null)
            : ResultWrapper<OtsContractCreator?>.Fail(ChainChanged, ErrorCodes.ResourceUnavailable);
    }

    private SearchResult<List<ParityLikeTxTrace>> ReadCreatorTraces(Block block, Address address, bool indexed, CancellationToken ct)
    {
        SearchResult<List<ParityLikeTxTrace>> stored = ReadTraces(block.Header);
        if (stored.IsError) return stored;
        if (OtsTraceReader.Validate(block, stored.Object!, ct) is { } error)
            return new(error, ErrorCodes.ResourceUnavailable);
        if (indexed)
        {
            SearchResult<bool[]> matches = MatchTransactions(block, stored.Object!, address, ct);
            if (matches.IsError) return new(matches.Error!, matches.ErrorCode);
            if (Array.IndexOf(matches.Object!, true) < 0)
                return new("Trace address index candidate does not match stored traces; reset the index.", ErrorCodes.ResourceUnavailable);
        }
        return stored;
    }

    private static SearchResult<OtsContractCreator> FindCreatorInBlock(Block block, List<ParityLikeTxTrace> traces,
        Address address, Stack<ParityTraceAction> stack, CancellationToken ct)
    {
        // Stored trace order is not part of the storage contract; use canonical transaction positions.
        ParityLikeTxTrace?[] ordered = new ParityLikeTxTrace?[block.Transactions.Length];
        foreach (ParityLikeTxTrace trace in traces)
            if (trace.TransactionHash is not null) ordered[trace.TransactionPosition!.Value] = trace;
        for (int index = ordered.Length - 1; index >= 0; index--)
        {
            ParityLikeTxTrace trace = ordered[index]!;
            SearchResult<Address> creator = FindSuccessfulCreator(trace.Action!, block.Transactions[index], address, stack, ct);
            if (creator.IsError) return new(creator.Error!, creator.ErrorCode);
            if (creator.Object is not null)
                return new(new OtsContractCreator { Hash = trace.TransactionHash!, Creator = creator.Object });
        }
        return default;
    }

    private static SearchResult<Address> FindSuccessfulCreator(ParityTraceAction root, Transaction transaction,
        Address address, Stack<ParityTraceAction> stack, CancellationToken ct)
    {
        stack.Clear();
        stack.Push(root);
        Address? creator = null;
        while (stack.TryPop(out ParityTraceAction? action))
        {
            ct.ThrowIfCancellationRequested();
            if (action.Error is not null) continue;
            if (action.Type == "create" || action.CallType == "init")
            {
                if (action.Result is null)
                    return new("Stored creation has no result.", ErrorCodes.ResourceUnavailable);
                Address? destination = GetDestination(action, root, transaction);
                if (destination is null)
                    return new("Stored successful creation has no destination.", ErrorCodes.ResourceUnavailable);
                if (destination == address)
                {
                    if (action.From is null)
                        return new("Stored creation has no creator.", ErrorCodes.ResourceUnavailable);
                    creator = action.From;
                }
            }
            for (int i = action.Subtraces.Count - 1; i >= 0; i--) stack.Push(action.Subtraces[i]);
        }
        return creator is null ? default : new(creator);
    }
}
