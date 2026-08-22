// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Receipts;

[Parallelizable(ParallelScope.All)]
public class ReceiptStorageRangeRemovalTests
{
    // The pruner calls this on whatever store the node was configured with, and NullReceiptStorage is what
    // Receipt.StoreReceipts=false binds. An implementation left on the throwing default aborts every pass after its
    // blocks are already gone, so the reclaim cursor never advances - which is the failure this whole change exists
    // to remove. Covering every implementation means the next one added is caught here rather than by an operator.
    private static IReceiptStorage[] AllImplementations() =>
    [
        NullReceiptStorage.Instance,
        new InMemoryReceiptStorage()
    ];

    [TestCaseSource(nameof(AllImplementations))]
    public void RemoveReceiptsRange_IsImplemented(IReceiptStorage storage) =>
        Assert.That(() => storage.RemoveReceiptsRange(1, 1000), Throws.Nothing,
            $"{storage.GetType().Name} inherits the throwing default, so a pruning node configured with it would fail every pass");

    [Test]
    public void InMemoryReceiptStorage_RemoveReceiptsRange_HoldsTheBounds()
    {
        InMemoryReceiptStorage storage = new();

        for (ulong number = 1; number <= 5; number++)
        {
            Block block = Build.A.Block.WithNumber(number).WithTransactions(Build.A.Transaction.TestObject).TestObject;
            storage.Insert(block, [Build.A.Receipt.WithBlockNumber(number).WithBlockHash(block.Hash).TestObject]);
            _blocks[number] = block;
        }

        storage.RemoveReceiptsRange(2, 4);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(storage.HasBlock(1, _blocks[1].Hash!), Is.True);
            Assert.That(storage.HasBlock(2, _blocks[2].Hash!), Is.False);
            Assert.That(storage.HasBlock(3, _blocks[3].Hash!), Is.False);
            Assert.That(storage.HasBlock(4, _blocks[4].Hash!), Is.True, "the upper bound is exclusive");
            Assert.That(storage.HasBlock(5, _blocks[5].Hash!), Is.True);
        }
    }

    private readonly System.Collections.Generic.Dictionary<ulong, Block> _blocks = [];
}
