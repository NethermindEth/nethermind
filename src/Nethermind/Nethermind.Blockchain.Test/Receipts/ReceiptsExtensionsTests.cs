// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Receipts;

[Parallelizable(ParallelScope.All)]
public class ReceiptsExtensionsTests
{
    [Test]
    public void SetSendersFromStoredReceipts_WhenReceiptsRecordSenders_SetsOnlyTheMissingOnes()
    {
        Block block = BlockWithoutSenders();
        block.Transactions[0].SenderAddress = TestItem.AddressD;
        InMemoryReceiptStorage storage = new();
        storage.Insert(block, [Receipt(TestItem.AddressA), Receipt(TestItem.AddressB), Receipt(null)]);

        storage.SetSendersFromStoredReceipts(block);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.Transactions[0].SenderAddress, Is.EqualTo(TestItem.AddressD), "a sender already set is kept");
            Assert.That(block.Transactions[1].SenderAddress, Is.EqualTo(TestItem.AddressB));
            Assert.That(block.Transactions[2].SenderAddress, Is.Null, "a receipt without a sender leaves it to recovery");
        }
    }

    [Test]
    public void SetSendersFromStoredReceipts_WhenReceiptCountDiffers_SetsNone()
    {
        Block block = BlockWithoutSenders();
        InMemoryReceiptStorage storage = new();
        storage.Insert(block, [Receipt(TestItem.AddressA), Receipt(TestItem.AddressB)]);

        storage.SetSendersFromStoredReceipts(block);

        Assert.That(block.Transactions, Has.All.Property(nameof(Transaction.SenderAddress)).Null);
    }

    [Test]
    public void SetSendersFromStoredReceipts_WhenNoReceiptsAreStored_SetsNone()
    {
        Block block = BlockWithoutSenders();

        new InMemoryReceiptStorage().SetSendersFromStoredReceipts(block);

        Assert.That(block.Transactions, Has.All.Property(nameof(Transaction.SenderAddress)).Null);
    }

    private static Block BlockWithoutSenders()
    {
        Transaction[] transactions = new Transaction[3];
        for (int i = 0; i < transactions.Length; i++)
        {
            transactions[i] = Build.A.Transaction.WithNonce((ulong)i).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            transactions[i].SenderAddress = null;
        }

        return Build.A.Block.WithTransactions(transactions).TestObject;
    }

    private static TxReceipt Receipt(Address? sender) => Build.A.Receipt.WithSender(sender!).TestObject;
}
