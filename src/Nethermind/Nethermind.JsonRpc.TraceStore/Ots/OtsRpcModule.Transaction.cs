// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.JsonRpc.TraceStore.Ots;

public sealed partial class OtsRpcModule
{
    /// <inheritdoc />
    public ResultWrapper<bool> ots_hasCode(Address address, BlockParameter block)
    {
        if (address is null || block is null)
            return ResultWrapper<bool>.Fail("An address and block are required.", ErrorCodes.InvalidParams);

        CancellationTokenSource timeout = rpcConfig.BuildTimeoutCancellationToken();
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            // The reference resolves pending to executed state and requires canonical hashes for code queries.
            BlockParameter selected = block.Type == BlockParameterType.Pending ? BlockParameter.Latest
                : block.BlockHash is { } hash ? new BlockParameter(hash, requireCanonical: true) : block;
            SearchResult<BlockHeader> search = blockFinder.SearchForHeader(selected);
            if (search.IsError) return ResultWrapper<bool>.Fail(search);
            BlockHeader header = search.Object!;
            if (blockFinder.Head is not { } head || header.Number > head.Number || !blockFinder.IsMainChain(header))
                return ResultWrapper<bool>.Fail("The selected block is not canonical and executed.", ErrorCodes.ResourceUnavailable);
            if (!stateReader.HasStateForBlock(header))
                return ResultWrapper<bool>.Fail($"State for block {header.Number} is unavailable.", ErrorCodes.ResourceUnavailable);

            bool hasCode = stateReader.TryGetAccount(header, address, out AccountStruct account) && account.HasCode;
            timeout.Token.ThrowIfCancellationRequested();
            if (!blockFinder.IsMainChain(header))
                return ResultWrapper<bool>.Fail(ChainChanged, ErrorCodes.ResourceUnavailable);
            return ResultWrapper<bool>.Success(hasCode);
        }
        finally { JsonRpcConfigExtension.ReturnTimeoutCancellationToken(timeout); }
    }

    /// <inheritdoc />
    public ResultWrapper<byte[]> ots_getTransactionError(Hash256 transactionHash)
    {
        if (transactionHash is null)
            return ResultWrapper<byte[]>.Fail("A transaction hash is required.", ErrorCodes.InvalidParams);

        CancellationTokenSource timeout = rpcConfig.BuildTimeoutCancellationToken();
        try
        {
            SearchResult<ParityLikeTxTrace> search = ReadTransactionTrace(transactionHash, timeout.Token);
            if (search.IsError) return ResultWrapper<byte[]>.Fail(search);
            ParityLikeTxTrace trace = search.Object!;
            if (trace.Action!.Error != "Reverted") return ResultWrapper<byte[]>.Success([]);
            byte[]? output = trace.Output ?? trace.Action.Result?.Output;
            return output is null
                ? ResultWrapper<byte[]>.Fail("Stored trace lacks the root revert output.", ErrorCodes.ResourceUnavailable)
                : ResultWrapper<byte[]>.Success(output);
        }
        finally { JsonRpcConfigExtension.ReturnTimeoutCancellationToken(timeout); }
    }

    /// <inheritdoc />
    public ResultWrapper<List<OtsInternalOperation>> ots_getInternalOperations(Hash256 transactionHash)
    {
        if (transactionHash is null)
            return ResultWrapper<List<OtsInternalOperation>>.Fail("A transaction hash is required.", ErrorCodes.InvalidParams);

        CancellationTokenSource timeout = rpcConfig.BuildTimeoutCancellationToken();
        try
        {
            SearchResult<ParityLikeTxTrace> search = ReadTransactionTrace(transactionHash, timeout.Token);
            if (search.IsError) return ResultWrapper<List<OtsInternalOperation>>.Fail(search);
            return ReadInternalOperations(search.Object!, timeout.Token);
        }
        finally { JsonRpcConfigExtension.ReturnTimeoutCancellationToken(timeout); }
    }

    private ResultWrapper<List<OtsInternalOperation>> ReadInternalOperations(ParityLikeTxTrace trace, CancellationToken ct)
    {
        List<OtsInternalOperation> operations = [];
        Stack<ParityTraceAction> stack = new();
        PushChildren(trace.Action!);
        while (stack.TryPop(out ParityTraceAction? action))
        {
            ct.ThrowIfCancellationRequested();
            if (ConvertInternalOperation(action, out OtsInternalOperation? operation) is { } error)
                return ResultWrapper<List<OtsInternalOperation>>.Fail(error, ErrorCodes.ResourceUnavailable);
            if (operation is not null) operations.Add(operation);
            PushChildren(action);
        }
        if (blockFinder.FindBlockHash(trace.BlockNumber) != trace.BlockHash)
            return ResultWrapper<List<OtsInternalOperation>>.Fail(ChainChanged, ErrorCodes.ResourceUnavailable);
        return ResultWrapper<List<OtsInternalOperation>>.Success(operations);

        void PushChildren(ParityTraceAction parent)
        {
            for (int i = parent.Subtraces.Count - 1; i >= 0; i--) stack.Push(parent.Subtraces[i]);
        }
    }

    private static string? ConvertInternalOperation(ParityTraceAction action, out OtsInternalOperation? operation)
    {
        operation = null;
        if (action.Type == "create" && action.CreationMethod is not (null or "create" or "create2"))
            return "Stored trace contains an unsupported creation method.";
        if (action is { Type: "create", To: null, From: not null, Result: null, Error: not null, Subtraces.Count: 0 })
            return null;
        int type = action.Type switch
        {
            "call" when action.CallType == "call" && !action.Value.IsZero => 0,
            "suicide" => 1,
            "create" when action.CreationMethod == "create2" => 3,
            "create" => 2,
            _ => -1
        };
        if (type >= 0)
        {
            Address? to = action.To ?? action.Result?.Address;
            if (action.From is null || to is null)
                return "Stored trace lacks an operation's sender or destination.";
            operation = new() { Type = type, From = action.From, To = to, Value = action.Value };
        }
        return null;
    }

    private SearchResult<ParityLikeTxTrace> ReadTransactionTrace(Hash256 transactionHash, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SearchResult<Hash256> blockHash = receiptFinder.SearchForReceiptBlockHash(transactionHash);
        if (blockHash.IsError) return new(blockHash.Error!, blockHash.ErrorCode);
        SearchResult<Block> search = blockFinder.SearchForBlock(new BlockParameter(blockHash.Object!, requireCanonical: true));
        if (search.IsError) return new(search.Error!, search.ErrorCode);
        Block block = search.Object!;
        SearchResult<List<ParityLikeTxTrace>> stored = ReadTraces(block.Header);
        if (stored.IsError) return new(stored.Error!, stored.ErrorCode);
        if (OtsTraceReader.Validate(block, stored.Object!, ct) is { } error)
            return new(error, ErrorCodes.ResourceUnavailable);

        foreach (ParityLikeTxTrace trace in stored.Object!)
        {
            ct.ThrowIfCancellationRequested();
            if (trace.TransactionHash != transactionHash) continue;
            return blockFinder.IsMainChain(block.Header)
                ? new(trace)
                : new(ChainChanged, ErrorCodes.ResourceUnavailable);
        }
        return new($"Stored trace for {transactionHash} is unavailable.", ErrorCodes.ResourceUnavailable);
    }

    /// <inheritdoc />
    public ResultWrapper<Hash256?> ots_getTransactionBySenderAndNonce(Address sender, ulong nonce)
    {
        if (sender is null)
            return ResultWrapper<Hash256?>.Fail("A sender address is required.", ErrorCodes.InvalidParams);
        if (blockFinder.Head is not { } head)
            return ResultWrapper<Hash256?>.Fail("Chain head is unavailable.", ErrorCodes.ResourceUnavailable);

        CancellationTokenSource timeout = rpcConfig.BuildTimeoutCancellationToken();
        try
        {
            if (CheckSenderNonce(sender, nonce, head.Header, timeout.Token, out bool used) is { } error)
                return ResultWrapper<Hash256?>.Fail(error, ErrorCodes.ResourceUnavailable);
            // EIP-7702 authorizations can consume nonces, but never decrease this upper bound.
            return used ? FindSenderTransaction(sender, nonce, head, timeout.Token) : ResultWrapper<Hash256?>.Success(null);
        }
        finally { JsonRpcConfigExtension.ReturnTimeoutCancellationToken(timeout); }
    }

    private string? CheckSenderNonce(Address sender, ulong nonce, BlockHeader head, CancellationToken ct, out bool used)
    {
        ct.ThrowIfCancellationRequested();
        used = false;
        if (!stateReader.HasStateForBlock(head)) return $"State for block {head.Number} is unavailable.";
        used = stateReader.TryGetAccount(head, sender, out AccountStruct account) && account.Nonce > nonce;
        ct.ThrowIfCancellationRequested();
        return blockFinder.IsMainChain(head) ? null : ChainChanged;
    }

    private ResultWrapper<Hash256?> FindSenderTransaction(Address sender, ulong nonce, Block head, CancellationToken ct)
    {
        Block? previous = null;
        for (ulong number = head.Number; number > 0; number--)
        {
            ct.ThrowIfCancellationRequested();
            Block? block = blockFinder.FindBlock(number);
            if (block is null)
                return ResultWrapper<Hash256?>.Fail($"Block {number} is unavailable.", ErrorCodes.ResourceUnavailable);
            if (!blockFinder.IsMainChain(block.Header) || (previous is not null && previous.ParentHash != block.Hash))
                return ResultWrapper<Hash256?>.Fail(ChainChanged, ErrorCodes.ResourceUnavailable);
            previous = block;
            if (FindSenderTransactionInBlock(block, sender, nonce, ct, out Transaction? transaction) is { } error)
                return ResultWrapper<Hash256?>.Fail(error, ErrorCodes.ResourceUnavailable);
            if (transaction is null) continue;
            if (!blockFinder.IsMainChain(head.Header) || !blockFinder.IsMainChain(block.Header))
                return ResultWrapper<Hash256?>.Fail(ChainChanged, ErrorCodes.ResourceUnavailable);
            return ResultWrapper<Hash256?>.Success(transaction.Hash);
        }
        ct.ThrowIfCancellationRequested();
        return blockFinder.IsMainChain(head.Header)
            ? ResultWrapper<Hash256?>.Success(null)
            : ResultWrapper<Hash256?>.Fail(ChainChanged, ErrorCodes.ResourceUnavailable);
    }

    private string? FindSenderTransactionInBlock(Block block, Address sender, ulong nonce, CancellationToken ct, out Transaction? match)
    {
        match = null;
        TxReceipt[]? receipts = null;
        for (int index = 0; index < block.Transactions.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            Transaction transaction = block.Transactions[index];
            if (transaction.Nonce != nonce) continue;
            Address? recoveredSender = transaction.SenderAddress;
            if (recoveredSender is null)
            {
                if (receipts is null)
                {
                    receipts = receiptFinder.Get(block);
                    if (ValidateReceipts(block, receipts, ct) is { } error) return error;
                }
                recoveredSender = receipts[index].Sender;
                if (recoveredSender is null) return "Transaction sender is unavailable.";
            }
            if (recoveredSender != sender) continue;
            match = transaction;
            return null;
        }
        return null;
    }
}
