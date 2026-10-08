// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc.Data;
using Nethermind.Serialization.Json;

namespace Nethermind.LightClient;

internal sealed class VerifiedExecutionData(IExecutionStateSource source, ISpecProvider specProvider, ulong chainId)
{
    private static readonly EthereumJsonSerializer Serializer = new();
    private readonly EthereumEcdsa _ecdsa = new(chainId);

    internal async Task<JsonElement> GetBlockAsync(BlockHeader header, bool fullTransactions, CancellationToken cancellationToken)
    {
        Block block = await source.GetBlockAsync(header, cancellationToken);
        if (fullTransactions) RecoverSenders(block);
        return ToJson(new BlockForRpc(block, fullTransactions, specProvider));
    }

    internal async Task<JsonElement> GetBlockTransactionCountAsync(BlockHeader header, CancellationToken cancellationToken)
    {
        Block block = await source.GetBlockAsync(header, cancellationToken);
        return JsonSerializer.SerializeToElement($"0x{block.Transactions.Length:x}");
    }

    internal async Task<JsonElement> GetTransactionAsync(BlockHeader header, int index, CancellationToken cancellationToken)
    {
        Block block = await source.GetBlockAsync(header, cancellationToken);
        if ((uint)index >= (uint)block.Transactions.Length) return JsonSerializer.SerializeToElement<object?>(null);
        Transaction transaction = block.Transactions[index];
        RecoverSender(transaction);
        TransactionForRpcContext context = new(chainId, header.Hash!, header.Number, index, header.Timestamp, header.BaseFeePerGas);
        return ToJson(TransactionForRpc.FromTransaction(transaction, context));
    }

    internal async Task<JsonElement> GetBlockReceiptsAsync(BlockHeader header, CancellationToken cancellationToken)
    {
        Block block = await source.GetBlockAsync(header, cancellationToken);
        TxReceipt[] receipts = await source.GetReceiptsAsync(block, cancellationToken);
        ReceiptForRpc[] result = BuildReceipts(block, receipts);
        return ToJson(result);
    }

    internal async Task<JsonElement> GetLogsAsync(BlockHeader[] headers, HashSet<Address>? addresses,
        IReadOnlyList<HashSet<Hash256>?> topics, CancellationToken cancellationToken)
    {
        List<LogEntryForRpc> result = [];
        foreach (BlockHeader header in headers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Block block = await source.GetBlockAsync(header, cancellationToken);
            TxReceipt[] receipts = await source.GetReceiptsAsync(block, cancellationToken);
            ReceiptForRpc[] rendered = BuildReceipts(block, receipts);
            foreach (ReceiptForRpc receipt in rendered)
            {
                if (receipt.Logs is null) continue;
                foreach (LogEntryForRpc log in receipt.Logs)
                {
                    if (addresses is not null && !addresses.Contains(log.Address)) continue;
                    if (!MatchesTopics(log.Topics, topics)) continue;
                    if (result.Count >= 10_000)
                        throw new RpcException(-32001, "Verified log query exceeds the 10000-log response limit.");
                    result.Add(log);
                }
            }
        }
        return ToJson(result);
    }

    private ReceiptForRpc[] BuildReceipts(Block block, TxReceipt[] receipts)
    {
        if (receipts.Length != block.Transactions.Length)
            throw new InvalidDataException("Verified receipt count does not match the block transaction count.");
        ReceiptForRpc[] result = new ReceiptForRpc[receipts.Length];
        ulong previousGas = 0;
        int logIndex = 0;
        IReleaseSpec releaseSpec = specProvider.GetSpec(block.Header);
        for (int i = 0; i < receipts.Length; i++)
        {
            Transaction transaction = block.Transactions[i];
            RecoverSender(transaction);
            TxReceipt receipt = receipts[i];
            if (receipt.TxType != transaction.Type || receipt.GasUsedTotal < previousGas || receipt.GasUsedTotal > block.GasUsed)
                throw new InvalidDataException("Verified receipt has inconsistent transaction type or cumulative gas.");
            receipt.TxHash = transaction.Hash;
            receipt.BlockHash = block.Hash;
            receipt.BlockNumber = block.Number;
            receipt.Index = i;
            receipt.GasUsed = receipt.GasUsedTotal - previousGas;
            receipt.Sender = transaction.SenderAddress;
            receipt.Recipient = transaction.To;
            if (transaction.CreatesTopLevelContract)
                receipt.ContractAddress = ContractAddress.From(transaction.SenderAddress, (Nethermind.Int256.UInt256)transaction.Nonce);
            result[i] = new ReceiptForRpc(transaction.Hash!, receipt, block.Timestamp,
                transaction.GetGasInfo(releaseSpec, block.Header), logIndex);
            previousGas = receipt.GasUsedTotal;
            logIndex = checked(logIndex + (receipt.Logs?.Length ?? 0));
        }
        return result;
    }

    private void RecoverSenders(Block block)
    {
        foreach (Transaction transaction in block.Transactions) RecoverSender(transaction);
    }

    private void RecoverSender(Transaction transaction)
    {
        transaction.SenderAddress ??= _ecdsa.RecoverAddress(transaction);
        if (transaction.SenderAddress is null)
            throw new InvalidDataException("Verified transaction has no recoverable sender.");
    }

    private static bool MatchesTopics(Hash256[] actual, IReadOnlyList<HashSet<Hash256>?> filter)
    {
        if (actual.Length < filter.Count) return false;
        for (int i = 0; i < filter.Count; i++)
            if (filter[i] is { } choices && !choices.Contains(actual[i])) return false;
        return true;
    }

    private static JsonElement ToJson<T>(T value)
    {
        using JsonDocument document = JsonDocument.Parse(Serializer.Serialize(value));
        return document.RootElement.Clone();
    }
}
