// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules;
using Nethermind.Specs;
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

    private static long?[] LogIndexes(TxReceipt[] receipts)
    {
        Transaction[] transactions = receipts.Select(static _ => Build.A.Transaction.SignedAndResolved().TestObject).ToArray();
        Block block = Build.A.Block.WithTransactions(transactions).TestObject;
        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();
        receiptFinder.Get(block).Returns(receipts);

        ReceiptForRpc[] result = receiptFinder.GetBlockReceipts(block, MainnetSpecProvider.Instance).Data!;

        return result.SelectMany(static r => r.Logs!).Select(static l => l.LogIndex).ToArray();
    }

    private static TxReceipt Receipt(int index, int logCount) =>
        Build.A.Receipt.WithIndex(index).WithLogs(Enumerable.Range(0, logCount).Select(static _ => Build.A.LogEntry.TestObject).ToArray()).TestObject;
}
