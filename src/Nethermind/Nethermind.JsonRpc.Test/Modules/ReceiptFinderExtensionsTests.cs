// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

[Parallelizable(ParallelScope.All)]
public class ReceiptFinderExtensionsTests
{
    [Test]
    public void GetBlockReceipts_WithPositionalIndexes_NumbersLogsAcrossTheBlock()
    {
        long?[] logIndexes = LogIndexes([Receipt(0, logCount: 1), Receipt(1, logCount: 0), Receipt(2, logCount: 2)]);

        Assert.That(logIndexes, Is.EqualTo(new long?[] { 0, 1, 2 }), "log indexes must run across receipts, skipping none for a receipt without logs");
    }

    // Indexes out of position must keep the per-receipt scan, which counts the logs of every receipt with a lower
    // index. The second receipt sits at its own position, so a per-receipt shortcut would number it from 1.
    [TestCase(new[] { 1, 0 }, new[] { 1, 2 }, new long[] { 2, 0, 1 }, TestName = "Swapped")]
    [TestCase(new[] { 1, 1 }, new[] { 1, 1 }, new long[] { 0, 0 }, TestName = "Duplicated")]
    public void GetBlockReceipts_WithIndexesOutOfPosition_CountsLogsOfLowerIndexes(int[] indexes, int[] logCounts, long[] expected)
    {
        TxReceipt[] receipts = indexes.Select((index, i) => Receipt(index, logCounts[i])).ToArray();

        long?[] logIndexes = LogIndexes(receipts);

        Assert.That(logIndexes, Is.EqualTo(expected.Select(static e => (long?)e).ToArray()), "each receipt must start after the logs of the receipts with a lower index");
    }

    // Receipts of every shape follow each other, so a field one of them sets cannot leak into the next.
    [TestCaseSource(nameof(SerializationCases))]
    public void GetBlockReceipts_SerializesAsEagerlyBuiltReceipts(TxReceipt[] receipts, bool eip8116)
    {
        Transaction[] transactions = receipts.Select(static r => Build.A.Transaction.WithType(r.TxType).WithShardBlobTxTypeAndFieldsIfBlobTx(isMempoolTx: false).SignedAndResolved().TestObject).ToArray();
        Block block = Build.A.Block.WithTransactions(transactions).WithTimestamp(12).WithExcessBlobGas(0).TestObject;
        ReleaseSpec spec = Cancun.Instance.Clone();
        spec.IsEip8116Enabled = eip8116;
        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();
        receiptFinder.Get(block).Returns(receipts);

        using ResultWrapper<IEnumerable<ReceiptForRpc>?> result = receiptFinder.GetBlockReceipts(block, new TestSpecProvider(spec));

        ReceiptForRpc[] expected = receipts.Select((r, i) => new ReceiptForRpc(transactions[i].Hash!, r, block.Timestamp,
            transactions[i].GetGasInfo(spec, block.Header), eip8116 ? 0 : receipts.GetBlockLogFirstIndex(r.Index))).ToArray();
        EthereumJsonSerializer serializer = new();
        string expectedJson = serializer.Serialize(expected);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(serializer.Serialize<object>(result.Data!), Is.EqualTo(expectedJson), "serialized result");
            Assert.That(serializer.Serialize(result.Data!.ToArray()), Is.EqualTo(expectedJson), "enumerated result");
        }
    }

    private static IEnumerable<TestCaseData> SerializationCases()
    {
        yield return new TestCaseData(Array.Empty<TxReceipt>(), false).SetName("Empty");
        yield return new TestCaseData(MixedReceipts(), false).SetName("Mixed");
        yield return new TestCaseData(MixedReceipts(), true).SetName("MixedEip8116");
        yield return new TestCaseData(new[] { Receipt(1, logCount: 1), Receipt(0, logCount: 2) }, false).SetName("OutOfPosition");
    }

    private static TxReceipt[] MixedReceipts()
    {
        TxReceipt frame = Receipt(0, logCount: 2);
        frame.TxType = TxType.FrameTx;
        frame.Payer = TestItem.AddressD;
        frame.FrameReceipts =
        [
            new TxFrameReceipt(TxFrameReceipt.StatusSuccess, executionGasUsed: 21_000, stateGasUsed: 0, logs: [frame.Logs![0]]),
            new TxFrameReceipt(TxFrameReceipt.StatusFailure, executionGasUsed: 5_000, stateGasUsed: 0, logs: [frame.Logs[1]]),
        ];
        TxReceipt blob = Receipt(1, logCount: 1);
        blob.TxType = TxType.Blob;
        TxReceipt creation = Receipt(2, logCount: 0);
        creation.TxType = TxType.EIP1559;
        creation.Recipient = null;
        creation.ContractAddress = TestItem.AddressC;
        TxReceipt preByzantium = Receipt(3, logCount: 1);
        preByzantium.PostTransactionState = TestItem.KeccakB;
        TxReceipt manyLogs = Receipt(4, logCount: 5);
        manyLogs.TxType = TxType.AccessList;
        return [frame, blob, creation, preByzantium, manyLogs, Receipt(5, logCount: 0)];
    }

    [Test]
    public void Serializing_block_receipts_does_not_allocate_per_receipt()
    {
        const int receiptCount = 256;
        using ResultWrapper<IEnumerable<ReceiptForRpc>?> result = GetBlockReceipts(Enumerable.Range(0, receiptCount).Select(static i => Receipt(i, logCount: 2)).ToArray());
        object receipts = result.Data!;
        ArrayBufferWriter<byte> buffer = new(1 << 20);
        Write(receipts, buffer);

        // The minimum over a few runs filters a one-off runtime allocation; per-receipt materialization would show in every run.
        long allocated = long.MaxValue;
        for (int i = 0; i < 5; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Write(receipts, buffer);
            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.That(allocated, Is.LessThan(receiptCount * 8), "block receipts must be written through one reused receipt");
    }

    private static void Write(object value, ArrayBufferWriter<byte> buffer)
    {
        buffer.ResetWrittenCount();
        using Utf8JsonWriter writer = new(buffer);
        TypeInfoJsonSerializer.Serialize(writer, value, value.GetType(), EthereumJsonSerializer.JsonOptions);
    }

    private static long?[] LogIndexes(TxReceipt[] receipts)
    {
        using ResultWrapper<IEnumerable<ReceiptForRpc>?> result = GetBlockReceipts(receipts);

        return result.Data!.SelectMany(static r => r.Logs!).Select(static l => l.LogIndex).ToArray();
    }

    private static ResultWrapper<IEnumerable<ReceiptForRpc>?> GetBlockReceipts(TxReceipt[] receipts)
    {
        Transaction[] transactions = receipts.Select(static _ => Build.A.Transaction.SignedAndResolved().TestObject).ToArray();
        Block block = Build.A.Block.WithTransactions(transactions).TestObject;
        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();
        receiptFinder.Get(block).Returns(receipts);

        return receiptFinder.GetBlockReceipts(block, MainnetSpecProvider.Instance);
    }

    private static TxReceipt Receipt(int index, int logCount) =>
        Build.A.Receipt.WithIndex(index).WithLogs(Enumerable.Range(0, logCount).Select(static _ => Build.A.LogEntry.TestObject).ToArray()).TestObject;
}
