// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

public class TransactionLengthCacheTests
{
    [Test]
    public void Length_is_cached([Values(TxType.Legacy, TxType.EIP1559, TxType.SetCode, TxType.Blob, TxType.FrameTx)] TxType type)
    {
        ITransactionSizeCalculator calculator = Substitute.For<ITransactionSizeCalculator>();
        calculator.GetLength(Arg.Any<Transaction>()).Returns(100);
        Transaction tx = Build.A.Transaction.WithType(type).TestObject;

        tx.GetLength(calculator);
        tx.GetLength(calculator);

        calculator.Received(1).GetLength(tx);
    }

    [Test]
    public void Network_length_counts_the_sidecar_only_when_the_type_has_a_mempool_form([Values(TxType.Legacy, TxType.AccessList, TxType.EIP1559, TxType.SetCode, TxType.Blob)] TxType type)
    {
        MempoolTransaction mempoolTx = Build.A.Transaction.WithType(type).WithAuthorizationCodeIfAuthorizationListTx()
            .WithShardBlobTxTypeAndFieldsIfBlobTx().SignedAndResolved().MempoolTestObject;

        Assert.That(mempoolTx.GetLength(), type == TxType.Blob
            ? Is.GreaterThan(mempoolTx.Transaction.GetLength())
            : Is.EqualTo(mempoolTx.Transaction.GetLength()));
    }
}
