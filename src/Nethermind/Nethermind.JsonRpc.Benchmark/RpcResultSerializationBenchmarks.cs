// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Filters;
using Nethermind.JsonRpc.Data;
using Nethermind.Serialization.Json;
using Nethermind.Specs;

namespace Nethermind.JsonRpc.Benchmark;

/// <summary>
/// Compares the source-generated metadata path with writers shaped like a Nethermind generator's output, on RPC results
/// that the metadata path serializes today.
/// </summary>
[MemoryDiagnoser]
public class RpcResultSerializationBenchmarks
{
    private readonly ArrayBufferWriter<byte> _buffer = new(1 << 20);
    private JsonSerializerOptions _baseline = null!;
    private JsonSerializerOptions _generated = null!;
    private BlockForRpc _block = null!;
    private IEnumerable<FilterLog> _logs = null!;
    private IEnumerable<ReceiptForRpc> _receipts = null!;

    [GlobalSetup]
    public void Setup()
    {
        _baseline = EthereumJsonSerializer.JsonOptions;
        _generated = new JsonSerializerOptions(_baseline);
        _generated.Converters.Insert(0, new GeneratedStyleTransactionWriter());
        _generated.Converters.Insert(0, new GeneratedStyleFilterLogWriter());
        _generated.Converters.Insert(0, new GeneratedStyleReceiptWriter());

        Block block = BuildBlock(200);
        _block = new BlockForRpc(block, includeFullTransactionData: true, MainnetSpecProvider.Instance);
        _logs = BuildLogs(1000);
        _receipts = BuildReceipts(block);

        string? dump = Environment.GetEnvironmentVariable("RPC_SER_DUMP");
        Check(_block, "block", dump);
        Check(_logs, "logs", dump);
        Check(_receipts, "receipts", dump);
    }

    [Benchmark(Baseline = true, Description = "getBlockByNumber(full, 200 txs): metadata")]
    public int BlockMetadata() => Write(_block, _baseline);

    [Benchmark(Description = "getBlockByNumber(full, 200 txs): generated-style tx writer")]
    public int BlockGenerated() => Write(_block, _generated);

    [Benchmark(Description = "getLogs(1000 logs): metadata")]
    public int LogsMetadata() => Write(_logs, _baseline);

    [Benchmark(Description = "getLogs(1000 logs): generated-style log writer")]
    public int LogsGenerated() => Write(_logs, _generated);

    [Benchmark(Description = "getBlockReceipts(200 receipts): metadata")]
    public int ReceiptsMetadata() => Write(_receipts, _baseline);

    [Benchmark(Description = "getBlockReceipts(200 receipts): generated-style receipt writer")]
    public int ReceiptsGenerated() => Write(_receipts, _generated);

    private void Check<T>(T value, string name, string? dump)
    {
        Write(value, _baseline);
        byte[] expected = _buffer.WrittenSpan.ToArray();
        Write(value, _generated);
        byte[] actual = _buffer.WrittenSpan.ToArray();
        if (dump is not null)
        {
            File.WriteAllBytes($"{dump}.{name}.json", expected);
            File.WriteAllBytes($"{dump}.{name}.generated.json", actual);
        }

        if (!expected.AsSpan().SequenceEqual(actual))
        {
            throw new InvalidOperationException($"{name}: generated-style output differs from the metadata path");
        }
    }

    private int Write<T>(T value, JsonSerializerOptions options)
    {
        _buffer.ResetWrittenCount();
        using (Utf8JsonWriter writer = new(_buffer, new JsonWriterOptions { SkipValidation = true, Encoder = options.Encoder }))
        {
            TypeInfoJsonSerializer.Serialize(writer, value, options);
        }

        return _buffer.WrittenCount;
    }

    private static IEnumerable<FilterLog> BuildLogs(int count)
    {
        FilterLog[] logs = new FilterLog[count];
        Random random = new(42);
        for (int i = 0; i < count; i++)
        {
            byte[] data = new byte[64];
            random.NextBytes(data);
            Hash256[] topics = [TestItem.Keccaks[i % TestItem.Keccaks.Length], TestItem.KeccakA, TestItem.KeccakB];
            logs[i] = new FilterLog(i, 25_000_000UL + (ulong)(i / 100), 1_700_000_000UL, TestItem.KeccakC, i % 200, TestItem.KeccakD,
                TestItem.Addresses[i % TestItem.Addresses.Length], data, topics);
        }

        return logs;
    }

    private static IEnumerable<ReceiptForRpc> BuildReceipts(Block block)
    {
        ReceiptForRpc[] receipts = new ReceiptForRpc[block.Transactions.Length];
        int logIndex = 0;
        for (int i = 0; i < receipts.Length; i++)
        {
            Transaction tx = block.Transactions[i];
            LogEntry[] logs = i % 2 == 0
                ? [Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA, TestItem.KeccakB).WithData(new byte[64]).TestObject]
                : [];
            TxReceipt receipt = Build.A.Receipt
                .WithTxType(tx.Type)
                .WithTransactionHash(tx.Hash)
                .WithBlockNumber(25_000_000)
                .WithBlockHash(TestItem.KeccakC)
                .WithIndex(i)
                .WithGasUsed(21_000)
                .WithGasUsedTotal(21_000UL * (ulong)(i + 1))
                .WithSender(tx.SenderAddress!)
                .WithRecipient(tx.To)
                .WithStatusCode(1)
                .WithLogs(logs)
                .WithCalculatedBloom()
                .TestObject;
            receipts[i] = new ReceiptForRpc(tx.Hash!, receipt, 1_700_000_000UL, new TxGasInfo(10 * Unit.GWei, null, null), logIndex);
            logIndex += logs.Length;
        }

        return receipts;
    }

    internal static Block BuildBlock(int count)
    {
        Transaction[] transactions = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            int callDataLength = (i % 20) switch
            {
                < 10 => 0,
                < 14 => 68,
                < 17 => 260,
                < 19 => 1024,
                _ => 8192,
            };

            TransactionBuilder<Transaction> builder = Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithChainId(BlockchainIds.Mainnet)
                .WithNonce((ulong)i)
                .WithGasLimit(callDataLength == 0 ? 21_000UL : 90_000UL + (ulong)callDataLength * 16)
                .WithMaxFeePerGas(30 * Unit.GWei)
                .WithMaxPriorityFeePerGas(1 * Unit.GWei)
                .WithValue(1 * Unit.Ether)
                .WithTo(TestItem.Addresses[i % TestItem.Addresses.Length]);

            if (callDataLength > 0)
            {
                byte[] callData = new byte[callDataLength];
                new Random(i).NextBytes(callData);
                builder = builder.WithData(callData);
            }

            transactions[i] = builder.SignedAndResolved().TestObject;
        }

        return Build.A.Block.WithNumber(25_000_000).WithBaseFeePerGas(10 * Unit.GWei).WithTransactions(transactions).TestObject;
    }
}
