// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

public class TransactionLengthCacheTests
{
    [TestCase(TxType.Legacy)]
    [TestCase(TxType.EIP1559)]
    [TestCase(TxType.SetCode)]
    public void Length_without_blobs_is_cached_when_the_type_has_no_mempool_form(TxType type)
    {
        ITransactionSizeCalculator calculator = Substitute.For<ITransactionSizeCalculator>();
        calculator.GetLength(Arg.Any<Transaction>(), Arg.Any<bool>()).Returns(100);
        Transaction tx = Build.A.Transaction.WithType(type).TestObject;

        tx.GetLength(calculator, shouldCountBlobs: false);
        tx.GetLength(calculator, shouldCountBlobs: false);
        tx.GetLength(calculator, shouldCountBlobs: true);

        calculator.Received(1).GetLength(tx, Arg.Any<bool>());
    }

    [TestCase(TxType.Legacy)]
    [TestCase(TxType.AccessList)]
    [TestCase(TxType.EIP1559)]
    [TestCase(TxType.SetCode)]
    public void Both_lengths_are_equal_when_the_type_has_no_mempool_form(TxType type)
    {
        NetworkTransactionSizeCalculator calculator = new(TxDecoder.Instance);
        Transaction tx = Build.A.Transaction.WithType(type).WithAuthorizationCodeIfAuthorizationListTx().SignedAndResolved().TestObject;

        Assert.That(calculator.GetLength(tx, shouldCountBlobs: false), Is.EqualTo(calculator.GetLength(tx, shouldCountBlobs: true)));
    }

    [TestCase(TxType.Blob)]
    [TestCase(TxType.FrameTx)]
    public void Length_without_blobs_is_computed_when_the_type_has_a_mempool_form(TxType type)
    {
        ITransactionSizeCalculator calculator = Substitute.For<ITransactionSizeCalculator>();
        calculator.GetLength(Arg.Any<Transaction>(), true).Returns(200);
        calculator.GetLength(Arg.Any<Transaction>(), false).Returns(100);
        Transaction tx = Build.A.Transaction.WithType(type).TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tx.GetLength(calculator, shouldCountBlobs: true), Is.EqualTo(200));
            Assert.That(tx.GetLength(calculator, shouldCountBlobs: false), Is.EqualTo(100));
        }
    }
}
