// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Comparers;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.TxPool.Comparison;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class BlobTxPriorityComparerTests
{
    [Test]
    public void Prefers_higher_blob_fee_cap()
    {
        Transaction higher = BlobTransaction(120);
        Transaction lower = BlobTransaction(100);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(BlobTxPriorityComparer.Instance.Compare(higher, lower), Is.EqualTo(TxComparisonResult.XFirst));
            Assert.That(BlobTxPriorityComparer.Instance.Compare(lower, higher), Is.EqualTo(TxComparisonResult.YFirst));
        }
    }

    [Test]
    public void Equal_blob_fee_caps_are_not_decided() =>
        Assert.That(
            BlobTxPriorityComparer.Instance.Compare(BlobTransaction(100), BlobTransaction(100)),
            Is.EqualTo(TxComparisonResult.Equal));

    [Test]
    public void Prefers_blob_transaction_over_non_blob_transaction()
    {
        Transaction blob = BlobTransaction(100);
        Transaction nonBlob = Build.A.Transaction.TestObject;

        Assert.That(BlobTxPriorityComparer.Instance.Compare(blob, nonBlob), Is.EqualTo(TxComparisonResult.XFirst));
    }

    private static Transaction BlobTransaction(int maxFeePerBlobGas) =>
        Build.A.Transaction
            .WithShardBlobTxTypeAndFields(1)
            .WithMaxFeePerBlobGas(maxFeePerBlobGas.Wei)
            .TestObject;
}
