// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Receipts;

[Parallelizable(ParallelScope.All)]
public class ReceiptStorageRangeRemovalTests
{
    // Receipt.StoreReceipts=false binds NullReceiptStorage, and the throwing default would abort every pass.
    private static IReceiptStorage[] NonPersistentImplementations() =>
    [
        NullReceiptStorage.Instance,
        new InMemoryReceiptStorage()
    ];

    [TestCaseSource(nameof(NonPersistentImplementations))]
    public void RemoveReceiptsRange_IsImplemented(IReceiptStorage storage) =>
        Assert.That(() => storage.RemoveReceiptsRange(1, 1000), Throws.Nothing,
            $"{storage.GetType().Name} inherits the throwing default, so a pruning node configured with it would fail every pass");

    [Test]
    public void InMemoryReceiptStorage_RemoveReceiptsRange_HoldsTheBounds()
    {
        InMemoryReceiptStorage storage = new();
        Dictionary<ulong, Block> blocks = [];

        for (ulong number = 1; number <= 5; number++)
        {
            Block block = Build.A.Block.WithNumber(number).WithTransactions(Build.A.Transaction.TestObject).TestObject;
                storage.Insert(block, [Build.A.Receipt.WithBlockHash(block.Hash).TestObject]);
            blocks[number] = block;
        }

        storage.RemoveReceiptsRange(2, 4);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(storage.HasBlock(1, blocks[1].Hash!), Is.True);
            Assert.That(storage.HasBlock(2, blocks[2].Hash!), Is.False);
            Assert.That(storage.HasBlock(3, blocks[3].Hash!), Is.False);
            Assert.That(storage.HasBlock(4, blocks[4].Hash!), Is.True, "the upper bound is exclusive");
            Assert.That(storage.HasBlock(5, blocks[5].Hash!), Is.True);
        }
    }
}
