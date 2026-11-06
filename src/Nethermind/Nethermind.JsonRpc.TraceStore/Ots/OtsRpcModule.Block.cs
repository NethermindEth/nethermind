// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.TraceStore.Ots;

public sealed partial class OtsRpcModule
{
    /// <inheritdoc />
    public ResultWrapper<OtsBlockDetails?> ots_getBlockDetails(BlockParameter block) => GetBlockDetails(block);

    /// <inheritdoc />
    public ResultWrapper<OtsBlockDetails?> ots_getBlockDetailsByHash(Hash256 blockHash) => blockHash is null
        ? ResultWrapper<OtsBlockDetails?>.Fail("A block hash is required.", ErrorCodes.InvalidParams)
        : GetBlockDetails(new BlockParameter(blockHash));

    private ResultWrapper<OtsBlockDetails?> GetBlockDetails(BlockParameter parameter)
    {
        if (parameter is null)
            return ResultWrapper<OtsBlockDetails?>.Fail("A block is required.", ErrorCodes.InvalidParams);

        CancellationTokenSource timeout = rpcConfig.BuildTimeoutCancellationToken();
        try
        {
            CancellationToken ct = timeout.Token;
            ct.ThrowIfCancellationRequested();
            SearchResult<Block> search = FindOtsBlock(parameter);
            if (search.IsError) return ResultWrapper<OtsBlockDetails?>.Fail(search);
            if (search.Object is not { } block) return ResultWrapper<OtsBlockDetails?>.Success(null);
            TxReceipt[] receipts = receiptFinder.Get(block);
            if (ValidateReceipts(block, receipts, ct) is { } receiptError)
                return ResultWrapper<OtsBlockDetails?>.Fail(receiptError, ErrorCodes.ResourceUnavailable);

            SearchResult<OtsIssuance> issuance = ReadIssuance(block, ct);
            if (issuance.IsError) return ResultWrapper<OtsBlockDetails?>.Fail(issuance);
            IReleaseSpec spec = specProvider.GetSpec(block.Header);
            UInt256 fees = UInt256.Zero;
            for (int i = 0; i < receipts.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                TxGasInfo gas = block.Transactions[i].GetGasInfo(spec, block.Header);
                UInt256? price = gas.EffectiveGasPrice ?? receipts[i].EffectiveGasPrice;
                if (price is null)
                    return ResultWrapper<OtsBlockDetails?>.Fail("Effective gas price is unavailable.", ErrorCodes.ResourceUnavailable);
                fees += price.Value * receipts[i].GasUsed;
            }
            Dictionary<string, object?> summary = SummarizeBlock(block, parameter);
            ct.ThrowIfCancellationRequested();
            if (!StillSelected(block, parameter))
                return ResultWrapper<OtsBlockDetails?>.Fail(ChainChanged, ErrorCodes.ResourceUnavailable);
            return ResultWrapper<OtsBlockDetails?>.Success(new() { Block = summary, Issuance = issuance.Object!, TotalFees = fees });
        }
        finally { JsonRpcConfigExtension.ReturnTimeoutCancellationToken(timeout); }
    }

    /// <inheritdoc />
    public ResultWrapper<OtsBlockTransactions?> ots_getBlockTransactions(BlockParameter block, byte pageNumber, byte pageSize)
    {
        if (block is null)
            return ResultWrapper<OtsBlockTransactions?>.Fail("A block is required.", ErrorCodes.InvalidParams);

        CancellationTokenSource timeout = rpcConfig.BuildTimeoutCancellationToken();
        try
        {
            CancellationToken ct = timeout.Token;
            ct.ThrowIfCancellationRequested();
            SearchResult<Block> search = FindOtsBlock(block);
            if (search.IsError) return ResultWrapper<OtsBlockTransactions?>.Fail(search);
            if (search.Object is not { } selected) return ResultWrapper<OtsBlockTransactions?>.Success(null);
            TxReceipt[] receipts = receiptFinder.Get(selected);
            if (ValidateReceipts(selected, receipts, ct) is { } error)
                return ResultWrapper<OtsBlockTransactions?>.Fail(error, ErrorCodes.ResourceUnavailable);

            // Reference pages count backwards, but preserve ascending indices within each page.
            int end = Math.Max(0, selected.Transactions.Length - pageNumber * pageSize);
            int start = Math.Max(0, end - pageSize);
            TransactionForRpc[] transactions = new TransactionForRpc[end - start];
            Dictionary<string, object?>[] pageReceipts = new Dictionary<string, object?>[end - start];
            IReleaseSpec spec = specProvider.GetSpec(selected.Header);
            for (int i = start; i < end; i++)
            {
                ct.ThrowIfCancellationRequested();
                Transaction tx = selected.Transactions[i];
                TxReceipt receipt = receipts[i];
                if (receipt.Sender is null || (tx.SenderAddress is { } sender && sender != receipt.Sender)
                    || receipt.Recipient != tx.To || receipt.TxType != tx.Type)
                    return ResultWrapper<OtsBlockTransactions?>.Fail("Stored receipt transaction metadata is inconsistent.", ErrorCodes.ResourceUnavailable);
                tx.SenderAddress ??= receipt.Sender;
                TransactionForRpc rpcTx = TransactionForRpc.FromTransaction(tx, new(specProvider.ChainId,
                    selected.Hash!, selected.Number, i, selected.Timestamp, selected.BaseFeePerGas, receipt));
                if (rpcTx is not LegacyTransactionForRpc legacy)
                    return ResultWrapper<OtsBlockTransactions?>.Fail("This transaction type does not support an OTS input selector.", ErrorCodes.ResourceUnavailable);
                if (legacy.Input is { Length: > 4 } input) legacy.Input = input.AsSpan(0, 4).ToArray();
                transactions[i - start] = rpcTx;
                ReceiptForRpc rpcReceipt = new(tx.Hash!, receipt, selected.Timestamp, tx.GetGasInfo(spec, selected.Header))
                {
                    Logs = null,
                    LogsBloom = null
                };
                Dictionary<string, object?> summaryReceipt = Snapshot(rpcReceipt);
                summaryReceipt["logs"] = null;
                summaryReceipt["logsBloom"] = null;
                pageReceipts[i - start] = summaryReceipt;
            }
            Dictionary<string, object?> summary = SummarizeBlock(selected, block);
            summary["transactions"] = (BlockTransactions)transactions;
            ct.ThrowIfCancellationRequested();
            if (!StillSelected(selected, block))
                return ResultWrapper<OtsBlockTransactions?>.Fail(ChainChanged, ErrorCodes.ResourceUnavailable);
            return ResultWrapper<OtsBlockTransactions?>.Success(new() { Fullblock = summary, Receipts = pageReceipts });
        }
        finally { JsonRpcConfigExtension.ReturnTimeoutCancellationToken(timeout); }
    }

    private SearchResult<Block> FindOtsBlock(BlockParameter parameter)
    {
        SearchResult<Block> result = blockFinder.SearchForBlock(parameter, allowNulls: true);
        if (!result.IsError && result.Object is null)
        {
            if (parameter.RequireCanonical && parameter.BlockHash is { } hash && blockFinder.FindHeader(hash) is { } header
                && !blockFinder.IsMainChain(header))
                return new("The selected block is not canonical.", ErrorCodes.InvalidInput);
            if (blockFinder.FindHeader(parameter) is not null)
                return new("The selected block body is unavailable.", ErrorCodes.ResourceUnavailable);
        }
        return result;
    }

    private bool StillSelected(Block block, BlockParameter parameter) =>
        parameter.Type == BlockParameterType.Pending
        || (parameter.Type == BlockParameterType.BlockHash && !parameter.RequireCanonical)
        || blockFinder.IsMainChain(block.Header);

    private SearchResult<OtsIssuance> ReadIssuance(Block block, CancellationToken ct)
    {
        // EIP-3675 removes execution-layer block and uncle rewards after the merge.
        if (block.IsGenesis || block.Header.IsPostMerge || !rewardCalculator.MayReward(block.Header))
            return new(new OtsIssuance());
        if ((traceStoreConfig.TraceTypes & ParityTraceTypes.Rewards) == 0)
            return new("Block issuance requires stored Rewards traces.", ErrorCodes.ResourceUnavailable);
        SearchResult<List<ParityLikeTxTrace>> stored = ReadTraces(block.Header);
        if (stored.IsError) return new(stored.Error!, stored.ErrorCode);
        if (OtsTraceReader.Validate(block, stored.Object!, ct) is { } error)
            return new(error, ErrorCodes.ResourceUnavailable);
        UInt256 blockReward = UInt256.Zero;
        UInt256 uncleReward = UInt256.Zero;
        bool authorSeen = false;
        List<Address> uncleAuthors = [];
        foreach (ParityLikeTxTrace trace in stored.Object!)
        {
            ct.ThrowIfCancellationRequested();
            if (trace.TransactionHash is not null) continue;
            if (trace.BlockHash != block.Hash || trace.BlockNumber != block.Number || trace.TransactionPosition is not null
                || trace.Action is not { Type: "reward", Author: not null } action)
                return new("Stored block rewards are inconsistent.", ErrorCodes.ResourceUnavailable);
            switch (action.RewardType)
            {
                case "block" when !authorSeen && action.Author == block.Beneficiary:
                    authorSeen = true;
                    blockReward += action.Value;
                    break;
                case "uncle":
                    uncleAuthors.Add(action.Author);
                    uncleReward += action.Value;
                    break;
                default: return new("Stored reward type cannot be categorized as OTS issuance.", ErrorCodes.ResourceUnavailable);
            }
        }
        if (!authorSeen || uncleAuthors.Count != block.Uncles.Length)
            return new("Stored block rewards are incomplete.", ErrorCodes.ResourceUnavailable);
        foreach (BlockHeader uncle in block.Uncles)
        {
            if (uncle.Beneficiary is not { } beneficiary || !uncleAuthors.Remove(beneficiary))
                return new("Stored uncle rewards are inconsistent.", ErrorCodes.ResourceUnavailable);
        }
        return new(new OtsIssuance { BlockReward = blockReward, UncleReward = uncleReward });
    }

    private Dictionary<string, object?> SummarizeBlock(Block block, BlockParameter parameter)
    {
        BlockForRpc rpcBlock = blockForRpcFactory.Create(block, false, specProvider, skipTxs: true);
        rpcBlock.LogsBloom = null;
        if (parameter.Type == BlockParameterType.Pending)
        {
            rpcBlock.Hash = null;
            rpcBlock.Nonce = null;
            rpcBlock.Number = null;
        }
        Dictionary<string, object?> summary = Snapshot(rpcBlock);
        summary.Remove("transactions");
        summary["transactionCount"] = (ulong)block.Transactions.Length;
        return summary;
    }

    // Preserve consensus plugins' RPC fields without copying their block models into an OTS-specific schema.
    private static Dictionary<string, object?> Snapshot(object value)
    {
        JsonElement json = JsonSerializer.SerializeToElement(value,
            EthereumJsonSerializer.JsonOptions.GetTypeInfo(value.GetType()));
        Dictionary<string, object?> fields = [];
        foreach (JsonProperty property in json.EnumerateObject()) fields.Add(property.Name, property.Value);
        return fields;
    }
}
