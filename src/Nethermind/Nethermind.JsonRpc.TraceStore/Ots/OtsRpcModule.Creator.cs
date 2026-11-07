// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
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
            timeout.Token.ThrowIfCancellationRequested();
            bool hasCode = stateReader.TryGetAccount(head.Header, address, out AccountStruct account) && account.HasCode;
            if (!blockFinder.IsMainChain(head.Header))
                return ResultWrapper<OtsContractCreator?>.Fail(ChainChanged, ErrorCodes.ResourceUnavailable);
            if (!hasCode) return ResultWrapper<OtsContractCreator?>.Success(null);
            if (specProvider.GetSpec(head.Header).IsEip7702Enabled)
            {
                byte[]? code = stateReader.GetCode(account.CodeHash);
                if (code is null)
                    return ResultWrapper<OtsContractCreator?>.Fail("Current account code is unavailable.", ErrorCodes.ResourceUnavailable);
                timeout.Token.ThrowIfCancellationRequested();
                if (!blockFinder.IsMainChain(head.Header))
                    return ResultWrapper<OtsContractCreator?>.Fail(ChainChanged, ErrorCodes.ResourceUnavailable);
                // EIP-7702 delegation is installed by authorization, not by a CREATE transaction.
                if (Eip7702Constants.IsDelegatedCode(code)) return ResultWrapper<OtsContractCreator?>.Success(null);
            }

            Block? previous = null;
            Stack<ParityTraceAction> stack = new();
            for (ulong number = head.Number; number > 0; number--)
            {
                timeout.Token.ThrowIfCancellationRequested();
                Block? block = blockFinder.FindBlock(number);
                if (block is null)
                    return ResultWrapper<OtsContractCreator?>.Fail($"Block {number} is unavailable.", ErrorCodes.ResourceUnavailable);
                if (!blockFinder.IsMainChain(block.Header) || (previous is not null && previous.ParentHash != block.Hash))
                    return ResultWrapper<OtsContractCreator?>.Fail(ChainChanged, ErrorCodes.ResourceUnavailable);
                previous = block;
                if (block.Transactions.Length == 0) continue;
                SearchResult<List<ParityLikeTxTrace>> stored = ReadTraces(block.Header);
                if (stored.IsError) return ResultWrapper<OtsContractCreator?>.Fail(stored);
                if (OtsTraceReader.Validate(block, stored.Object!, timeout.Token) is { } error)
                    return ResultWrapper<OtsContractCreator?>.Fail(error, ErrorCodes.ResourceUnavailable);

                // Stored trace order is not part of the storage contract; use canonical transaction positions.
                ParityLikeTxTrace?[] ordered = new ParityLikeTxTrace?[block.Transactions.Length];
                foreach (ParityLikeTxTrace trace in stored.Object!)
                    if (trace.TransactionHash is not null) ordered[trace.TransactionPosition!.Value] = trace;
                for (int index = ordered.Length - 1; index >= 0; index--)
                {
                    ParityLikeTxTrace trace = ordered[index]!;
                    stack.Clear();
                    stack.Push(trace.Action!);
                    Address? creator = null;
                    while (stack.TryPop(out ParityTraceAction? action))
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        if (action.Error is not null) continue;
                        if (action.Type == "create" || action.CallType == "init")
                        {
                            if (action.Result is null)
                                return ResultWrapper<OtsContractCreator?>.Fail("Stored creation has no result.", ErrorCodes.ResourceUnavailable);
                            Address? destination = GetDestination(action, trace.Action!, block.Transactions[index]);
                            if (destination is null)
                                return ResultWrapper<OtsContractCreator?>.Fail("Stored successful creation has no destination.", ErrorCodes.ResourceUnavailable);
                            if (destination == address)
                            {
                                if (action.From is null)
                                    return ResultWrapper<OtsContractCreator?>.Fail("Stored creation has no creator.", ErrorCodes.ResourceUnavailable);
                                creator = action.From;
                            }
                        }
                        for (int i = action.Subtraces.Count - 1; i >= 0; i--) stack.Push(action.Subtraces[i]);
                    }
                    if (creator is null) continue;
                    if (!blockFinder.IsMainChain(head.Header) || !blockFinder.IsMainChain(block.Header))
                        return ResultWrapper<OtsContractCreator?>.Fail(ChainChanged, ErrorCodes.ResourceUnavailable);
                    return ResultWrapper<OtsContractCreator?>.Success(new() { Hash = trace.TransactionHash!, Creator = creator });
                }
            }
            timeout.Token.ThrowIfCancellationRequested();
            return blockFinder.IsMainChain(head.Header)
                ? ResultWrapper<OtsContractCreator?>.Success(null)
                : ResultWrapper<OtsContractCreator?>.Fail(ChainChanged, ErrorCodes.ResourceUnavailable);
        }
        finally { JsonRpcConfigExtension.ReturnTimeoutCancellationToken(timeout); }
    }
}
