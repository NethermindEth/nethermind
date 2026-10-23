// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Core.Extensions;
using Nethermind.Consensus.Validators;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc.Data;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Logging;

namespace Nethermind.LightClient;

internal sealed class VerifiedExecutionData(IExecutionStateSource source, ISpecProvider specProvider, ulong chainId) : IDisposable
{
    private const int LogPrefetchLimit = 4;
    private readonly EthereumEcdsa _ecdsa = new(chainId);
    private readonly MemoryCache _transactionIndex = new(new MemoryCacheOptions { SizeLimit = 32_768 });

    internal JsonElement GetHeader(BlockHeader header)
    {
        IReleaseSpec spec = specProvider.GetSpec(header);
        Dictionary<string, object?> result = new()
        {
            ["difficulty"] = header.Difficulty,
            ["extraData"] = header.ExtraData,
            ["gasLimit"] = header.GasLimit,
            ["gasUsed"] = header.GasUsed,
            ["hash"] = header.Hash,
            ["logsBloom"] = header.Bloom,
            ["miner"] = header.Beneficiary,
            ["nonce"] = $"0x{header.Nonce:x16}",
            ["number"] = header.Number,
            ["parentHash"] = header.ParentHash,
            ["receiptsRoot"] = header.ReceiptsRoot,
            ["sha3Uncles"] = header.UnclesHash,
            ["stateRoot"] = header.StateRoot,
            ["timestamp"] = header.Timestamp,
            ["transactionsRoot"] = header.TxRoot,
        };
        if (header.MixHash is not null) result["mixHash"] = header.MixHash;
        if (spec.IsEip1559Enabled) result["baseFeePerGas"] = header.BaseFeePerGas;
        if (header.WithdrawalsRoot is not null) result["withdrawalsRoot"] = header.WithdrawalsRoot;
        if (header.RequestsHash is not null) result["requestsHash"] = header.RequestsHash;
        if (header.BlockAccessListHash is not null) result["blockAccessListHash"] = header.BlockAccessListHash;
        if (spec.IsEip4844Enabled)
        {
            if (header.BlobGasUsed is not null) result["blobGasUsed"] = header.BlobGasUsed;
            if (header.ExcessBlobGas is not null) result["excessBlobGas"] = header.ExcessBlobGas;
        }
        if (spec.IsEip4788Enabled && header.ParentBeaconBlockRoot is not null)
            result["parentBeaconBlockRoot"] = header.ParentBeaconBlockRoot;
        if (spec.IsEip7843Enabled && header.SlotNumber is not null) result["slotNumber"] = header.SlotNumber;
        return BoundedJson.ToElement(result);
    }

    internal async Task<JsonElement> GetBlockAsync(BlockHeader header, bool fullTransactions, CancellationToken cancellationToken)
    {
        Block block = await source.GetBlockAsync(header, cancellationToken);
        Index(block);
        if (fullTransactions)
        {
            long payloadBytes = 0;
            foreach (Transaction transaction in block.Transactions) payloadBytes += PayloadBytes(transaction);
            ValidatePayloadBytes(payloadBytes);
            RecoverSenders(block);
        }
        return BoundedJson.ToElement(new BlockForRpc(block, fullTransactions, specProvider));
    }

    internal async Task<JsonElement> GetBlockTransactionCountAsync(BlockHeader header, CancellationToken cancellationToken)
    {
        Block block = await source.GetBlockAsync(header, cancellationToken);
        Index(block);
        return JsonSerializer.SerializeToElement($"0x{block.Transactions.Length:x}");
    }

    internal async Task<JsonElement> GetTransactionAsync(BlockHeader header, int index, CancellationToken cancellationToken)
    {
        Block block = await source.GetBlockAsync(header, cancellationToken);
        Index(block);
        if ((uint)index >= (uint)block.Transactions.Length) return JsonSerializer.SerializeToElement<object?>(null);
        Transaction transaction = block.Transactions[index];
        ValidatePayloadBytes(PayloadBytes(transaction));
        RecoverSender(transaction);
        TransactionForRpcContext context = new(chainId, header.Hash!, header.Number, index, header.Timestamp, header.BaseFeePerGas);
        return BoundedJson.ToElement(TransactionForRpc.FromTransaction(transaction, context));
    }

    internal async Task<JsonElement> GetBlockReceiptsAsync(BlockHeader header, CancellationToken cancellationToken)
    {
        Block block = await source.GetBlockAsync(header, cancellationToken);
        Index(block);
        TxReceipt[] receipts = await source.GetReceiptsAsync(block, cancellationToken);
        long payloadBytes = 0;
        foreach (TxReceipt receipt in receipts) ValidateLogPayload(receipt, ref payloadBytes);
        ReceiptForRpc[] result = BuildReceipts(block, receipts);
        return BoundedJson.ToElement(result);
    }

    internal async Task<JsonElement> GetLogsAsync(BlockHeader[] headers, HashSet<Address>? addresses,
        IReadOnlyList<HashSet<Hash256>?> topics, CancellationToken cancellationToken)
    {
        List<LogEntryForRpc> result = [];
        long payloadBytes = 2;
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using ArrayPoolList<Task<(BlockHeader Header, Block Block, TxReceipt[] Receipts)>> pending = new(LogPrefetchLimit);
        try
        {
            int next = 0;
            while (next < headers.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                while (next < headers.Length && pending.Count < LogPrefetchLimit)
                {
                    if (stop.IsCancellationRequested) break;
                    BlockHeader header = headers[next++];
                    if (MayContainLogs(header.Bloom, addresses, topics)) pending.Add(FetchAsync(header));
                }
                await Task.WhenAll(pending);
                foreach (Task<(BlockHeader Header, Block Block, TxReceipt[] Receipts)> fetch in pending)
                {
                    (BlockHeader header, Block block, TxReceipt[] receipts) = await fetch;
                    Append(header, block, receipts);
                }
                pending.Clear();
            }
            return BoundedJson.ToElement(result);
        }
        finally
        {
            await stop.CancelAsync();
            await ((Task)Task.WhenAll(pending)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        async Task<(BlockHeader, Block, TxReceipt[])> FetchAsync(BlockHeader header)
        {
            try
            {
                Block block = await source.GetBlockAsync(header, stop.Token);
                Index(block);
                TxReceipt[] receipts = await source.GetReceiptsAsync(block, stop.Token);
                return (header, block, receipts);
            }
            catch
            {
                await stop.CancelAsync();
                throw;
            }
        }

        void Append(BlockHeader header, Block block, TxReceipt[] receipts)
        {
            if (receipts.Length != block.Transactions.Length)
                throw new InvalidDataException("Verified receipt count does not match the block transaction count.");
            ulong previousGas = 0;
            int logIndex = 0;
            for (int i = 0; i < receipts.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TxReceipt receipt = receipts[i];
                Transaction transaction = block.Transactions[i];
                if (receipt.TxType != transaction.Type || receipt.GasUsedTotal < previousGas)
                    throw new InvalidDataException("Verified receipt has inconsistent transaction type or cumulative gas.");
                previousGas = receipt.GasUsedTotal;
                foreach (LogEntry log in receipt.Logs ?? [])
                {
                    int index = logIndex;
                    logIndex = checked(logIndex + 1);
                    if (addresses is not null && !addresses.Contains(log.Address)) continue;
                    if (!MatchesTopics(log.Topics, topics)) continue;
                    if (result.Count >= 10_000)
                        throw new RpcException(-32001, "Verified log query exceeds the 10000-log response limit.");
                    payloadBytes += log.Data.LongLength * 2 + 4 + log.Topics.LongLength * 68;
                    if (payloadBytes > BoundedJson.ResultLimit) throw BoundedJson.TooLarge();
                    result.Add(new LogEntryForRpc
                    {
                        Removed = false,
                        LogIndex = index,
                        TransactionIndex = i,
                        TransactionHash = transaction.Hash!,
                        BlockHash = header.Hash!,
                        BlockNumber = header.Number,
                        BlockTimestamp = header.Timestamp,
                        Address = log.Address,
                        Data = log.Data,
                        Topics = log.Topics,
                    });
                }
            }
        }
    }

    internal bool TryLocateTransaction(Hash256 hash, out BlockHeader? header) =>
        _transactionIndex.TryGetValue(hash, out header);

    internal async Task<JsonElement?> FindTransactionAsync(BlockHeader header, Hash256 hash, bool receipt,
        CancellationToken cancellationToken)
    {
        Block block = await source.GetBlockAsync(header, cancellationToken);
        Index(block);
        for (int i = 0; i < block.Transactions.Length; i++)
        {
            Transaction transaction = block.Transactions[i];
            if (transaction.Hash != hash) continue;
            if (receipt)
            {
                TxReceipt[] receipts = await source.GetReceiptsAsync(block, cancellationToken);
                ReceiptForRpc[] result = BuildReceipts(block, receipts);
                long payloadBytes = 0;
                ValidateLogPayload(receipts[i], ref payloadBytes);
                return BoundedJson.ToElement(result[i]);
            }
            ValidatePayloadBytes(PayloadBytes(transaction));
            RecoverSender(transaction);
            return BoundedJson.ToElement(TransactionForRpc.FromTransaction(transaction,
                new(chainId, header.Hash!, header.Number, i, header.Timestamp, header.BaseFeePerGas)));
        }
        return null;
    }

    private void Index(Block block)
    {
        for (int i = 0; i < block.Transactions.Length; i++)
        {
            Transaction transaction = block.Transactions[i];
            transaction.Hash ??= transaction.CalculateHash();
            _transactionIndex.Set(transaction.Hash!, block.Header, new MemoryCacheEntryOptions
            {
                Size = 1,
                SlidingExpiration = TimeSpan.FromMinutes(20),
            });
        }
    }

    internal async Task<string> SendRawTransactionAsync(VerifiedHead head, byte[] raw, VerifiedCall? frameVerifier,
        CancellationToken cancellationToken)
    {
        if (raw.Length == 0) throw new RpcException(-32003, "Invalid transaction encoding.");
        Transaction transaction;
        try
        {
            RlpReader reader = new(raw.AsMemory());
            transaction = TxDecoder.Instance.DecodeCompleteNotNull(ref reader,
                RlpBehaviors.AllowUnsigned | RlpBehaviors.SkipTypedWrapping | RlpBehaviors.InMempoolForm);
        }
        catch (Exception exception) when (exception is RlpException or OverflowException or ArgumentException)
        {
            throw new RpcException(-32003, "Invalid transaction encoding.");
        }
        if (transaction.CarriesBlobs)
        {
            if (transaction.NetworkWrapper is not ShardBlobNetworkWrapper wrapper || !wrapper.HasFullBlobs())
                throw new RpcException(-32003, "Blob transaction submission requires a complete sidecar.");
            await KzgPolynomialCommitments.InitializeAsync(NullLogManager.Instance.GetClassLogger<VerifiedExecutionData>()).WaitAsync(cancellationToken);
        }
        BlockHeader header = await source.GetHeaderAsync(head, cancellationToken);
        IReleaseSpec spec = specProvider.GetSpec(header);
        ValidationResult validation = new TxValidator(chainId).IsWellFormed(transaction, spec, header.GasLimit);
        if (!validation) throw new RpcException(-32003, validation.Error!);
        if (transaction.SupportsFrames)
        {
            if (!FrameTxValidation.TryCalculateBlockGasReservations(transaction, spec, out ulong executionGas, out ulong stateGas)
                || executionGas > header.GasLimit || stateGas > header.GasLimit)
                throw new RpcException(-32003, "Frame transaction gas reservation exceeds the authenticated block gas limit.");
        }
        else if (transaction.GasLimit > header.GasLimit)
            throw new RpcException(-32003, "Transaction gas exceeds the authenticated block gas limit.");
        UInt256 feeCap = transaction.Supports1559 ? transaction.MaxFeePerGas : transaction.GasPrice;
        UInt256 nextBaseFee = BaseFeeCalculator.Calculate(header, specProvider.GetSpecFor1559(checked(header.Number + 1)));
        if (spec.IsEip1559Enabled && feeCap < nextBaseFee) throw new RpcException(-32003, "Transaction fee cap is below the next block base fee.");
        if (transaction.CarriesBlobs)
        {
            ulong nextExcess = BlobGasCalculator.CalculateExcessBlobGas(header, spec) ?? 0;
            if (!BlobGasCalculator.TryCalculateFeePerBlobGas(nextExcess, spec.BlobBaseFeeUpdateFraction, out UInt256 blobBaseFee)
                || transaction.MaxFeePerBlobGas < blobBaseFee)
                throw new RpcException(-32003, "Transaction blob fee cap is below the next block blob base fee.");
        }
        if (transaction.Type == TxType.FrameTx)
        {
            if (frameVerifier is null) throw new RpcException(-32601, "Verified frame admission is unavailable.");
            await frameVerifier.ValidateFrameAdmissionAsync(head, transaction, header, spec, cancellationToken);
            transaction.Hash ??= transaction.CalculateHash();
            await source.BroadcastTransactionAsync(transaction, cancellationToken);
            return transaction.Hash!.ToString();
        }
        RecoverSender(transaction);
        Account sender = await source.GetAccountAsync(head, transaction.SenderAddress!, cancellationToken);
        if (transaction.Nonce < sender.Nonce) throw new RpcException(-32003, "Transaction nonce is below authenticated state.");
        if (UInt256.MultiplyOverflow(feeCap, transaction.GasLimit, out UInt256 gasCost)
            || UInt256.AddOverflow(gasCost, transaction.Value, out UInt256 cost)
            || transaction.CarriesBlobs && (!BlobGasCalculator.TryCalculateBlobMaxFee(transaction.GetBlobCount(), transaction.MaxFeePerBlobGas ?? UInt256.Zero, out UInt256 blobCost)
                || UInt256.AddOverflow(cost, blobCost, out cost)) || cost > sender.Balance)
            throw new RpcException(-32003, "Transaction exceeds the sender's authenticated balance.");
        if (sender.HasCode)
        {
            byte[] code = await source.GetCodeAsync(sender, cancellationToken);
            if (!spec.IsEip7702Enabled || code.Length != 23 || code[0] != 0xef || code[1] != 0x01 || code[2] != 0x00)
                throw new RpcException(-32003, "Transaction sender has deployed code.");
        }
        transaction.Hash ??= transaction.CalculateHash();
        await source.BroadcastTransactionAsync(transaction, cancellationToken);
        return transaction.Hash!.ToString();
    }

    internal async Task<JsonElement> GetFeeHistoryAsync(BlockHeader[] headers, double[]? percentiles, CancellationToken cancellationToken)
    {
        string[] baseFees = new string[headers.Length + 1];
        string[] blobFees = new string[headers.Length + 1];
        double[] gasRatios = new double[headers.Length];
        double[] blobRatios = new double[headers.Length];
        string[][]? rewards = percentiles is null ? null : new string[headers.Length][];
        for (int i = 0; i < headers.Length; i++)
        {
            BlockHeader header = headers[i];
            IReleaseSpec spec = specProvider.GetSpec(header);
            baseFees[i] = VerifiedRpc.Quantity(header.BaseFeePerGas);
            gasRatios[i] = header.GasLimit == 0 ? 0 : header.GasUsed / (double)header.GasLimit;
            UInt256 blobFee = UInt256.Zero;
            if (header.ExcessBlobGas is not null && !BlobGasCalculator.TryCalculateFeePerBlobGas(header, spec.BlobBaseFeeUpdateFraction, out blobFee))
                throw new InvalidDataException("Blob base fee exceeds the supported integer range.");
            blobFees[i] = VerifiedRpc.Quantity(blobFee);
            blobRatios[i] = spec.GasCosts.MaxBlobGasPerBlock == 0 ? 0 : (header.BlobGasUsed ?? 0) / (double)spec.GasCosts.MaxBlobGasPerBlock;
            if (rewards is not null)
            {
                Block block = await source.GetBlockAsync(header, cancellationToken);
                Index(block);
                TxReceipt[] receipts = await source.GetReceiptsAsync(block, cancellationToken);
                rewards[i] = Rewards(block, receipts, percentiles!);
            }
        }
        BlockHeader newest = headers[^1];
        IReleaseSpec newestSpec = specProvider.GetSpec(newest);
        baseFees[^1] = VerifiedRpc.Quantity(BaseFeeCalculator.Calculate(newest, specProvider.GetSpecFor1559(newest.Number + 1)));
        ulong? nextExcess = BlobGasCalculator.CalculateExcessBlobGas(newest, newestSpec);
        UInt256 nextBlobFee = UInt256.Zero;
        if (nextExcess is ulong excess && !BlobGasCalculator.TryCalculateFeePerBlobGas(excess, newestSpec.BlobBaseFeeUpdateFraction, out nextBlobFee))
            throw new InvalidDataException("Blob base fee exceeds the supported integer range.");
        blobFees[^1] = VerifiedRpc.Quantity(nextBlobFee);
        object result = rewards is null
            ? new
            {
                oldestBlock = VerifiedRpc.Quantity(headers[0].Number),
                baseFeePerGas = baseFees,
                gasUsedRatio = gasRatios,
                baseFeePerBlobGas = blobFees,
                blobGasUsedRatio = blobRatios
            }
            : new
            {
                oldestBlock = VerifiedRpc.Quantity(headers[0].Number),
                baseFeePerGas = baseFees,
                gasUsedRatio = gasRatios,
                baseFeePerBlobGas = blobFees,
                blobGasUsedRatio = blobRatios,
                reward = rewards
            };
        return BoundedJson.ToElement(result);
    }

    private static string[] Rewards(Block block, TxReceipt[] receipts, double[] percentiles)
    {
        if (receipts.Length != block.Transactions.Length)
            throw new InvalidDataException("Verified receipt count does not match the block transaction count.");
        (UInt256 Tip, ulong Gas)[] ordered = new (UInt256, ulong)[block.Transactions.Length];
        ulong previous = 0;
        for (int i = 0; i < ordered.Length; i++)
        {
            if (receipts[i].TxType != block.Transactions[i].Type || receipts[i].GasUsedTotal < previous)
                throw new InvalidDataException("Verified receipt has inconsistent transaction type or cumulative gas.");
            block.Transactions[i].TryCalculatePremiumPerGas(block.BaseFeePerGas, out UInt256 tip);
            ordered[i] = (tip, receipts[i].GasUsedTotal - previous);
            previous = receipts[i].GasUsedTotal;
        }
        Array.Sort(ordered, static (left, right) => left.Tip.CompareTo(right.Tip));
        string[] result = new string[percentiles.Length];
        int index = 0;
        ulong consumed = ordered.Length == 0 ? 0 : ordered[0].Gas;
        for (int i = 0; i < result.Length; i++)
        {
            ulong threshold = (ulong)(previous * percentiles[i] / 100);
            while (index + 1 < ordered.Length && consumed < threshold) consumed += ordered[++index].Gas;
            result[i] = VerifiedRpc.Quantity(ordered.Length == 0 ? UInt256.Zero : ordered[index].Tip);
        }
        return result;
    }

    public void Dispose() => _transactionIndex.Dispose();

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
            if (receipt.TxType != transaction.Type || receipt.GasUsedTotal < previousGas)
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

    private static bool MayContainLogs(Bloom? bloom, HashSet<Address>? addresses, IReadOnlyList<HashSet<Hash256>?> topics)
    {
        if (bloom is null || bloom.IsZeroLength) return true;
        if (addresses is not null)
        {
            bool matches = false;
            foreach (Address address in addresses)
                if (bloom.Matches(address.Bytes)) { matches = true; break; }
            if (!matches) return false;
        }
        foreach (HashSet<Hash256>? choices in topics)
        {
            if (choices is null) continue;
            bool matches = false;
            foreach (Hash256 topic in choices)
                if (bloom.Matches(topic.Bytes)) { matches = true; break; }
            if (!matches) return false;
        }
        return true;
    }

    private static long PayloadBytes(Transaction transaction)
    {
        long bytes = transaction.DataLength;
        if (transaction.Frames is { } frames)
            foreach (TxFrame frame in frames) bytes += frame.Data.Length;
        if (transaction.FrameSignatures is { } signatures)
            foreach (TxFrameSignature signature in signatures) bytes += signature.Signature.Length + signature.Msg.Length;
        return bytes;
    }

    private static void ValidatePayloadBytes(long bytes)
    {
        if (bytes * 2 > BoundedJson.ResultLimit) throw BoundedJson.TooLarge();
    }

    private static void ValidateLogPayload(TxReceipt receipt, ref long bytes)
    {
        foreach (LogEntry log in receipt.Logs ?? [])
        {
            bytes += log.Data.LongLength * 2 + 4 + log.Topics.LongLength * 68;
            if (bytes > BoundedJson.ResultLimit) throw BoundedJson.TooLarge();
        }
    }
}
