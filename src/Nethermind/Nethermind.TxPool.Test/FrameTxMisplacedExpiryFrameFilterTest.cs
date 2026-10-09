// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs.Forks;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.TxPool.Filters;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.Core.Test.Builders.FrameTxTestFrames;

namespace Nethermind.TxPool.Test;

// The cases assert against the shared rejection counter.
[NonParallelizable]
internal class FrameTxMisplacedExpiryFrameFilterTest
{
    private static IEnumerable<TestCaseData> PlacementCases()
    {
        yield return new TestCaseData(new[] { SelfVerify(), Execution() }, AcceptTxResult.Accepted)
            .SetName("a transaction with no expiry frame is admissible");
        yield return new TestCaseData(new[] { Expiry(), SelfVerify(), Execution() }, AcceptTxResult.Accepted)
            .SetName("a leading expiry frame is admissible");
        yield return new TestCaseData(new[] { SelfVerify(), Expiry() }, AcceptTxResult.FrameTxMisplacedExpiryFrame)
            .SetName("an expiry frame behind the leading frame is rejected");
        // The other placement filter never sees this one: the layout matches no recognized prefix.
        yield return new TestCaseData(new[] { Execution(), Expiry(), SelfVerify() }, AcceptTxResult.FrameTxMisplacedExpiryFrame)
            .SetName("an expiry frame inside an unrecognized layout is rejected");
    }

    [TestCaseSource(nameof(PlacementCases))]
    public void Accept_RejectsAnExpiryFrameThatDoesNotLeadTheFrameList(TxFrame[] frames, AcceptTxResult expected)
    {
        long before = Metrics.PendingTransactionsFrameTxMisplacedExpiryFrame;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Accept(FrameTx(frames)), Is.EqualTo(expected));
            Assert.That(Metrics.PendingTransactionsFrameTxMisplacedExpiryFrame,
                Is.EqualTo(expected == AcceptTxResult.Accepted ? before : before + 1));
        }
    }

    [Test]
    public void Accept_LeavesANonFrameTransactionAlone()
    {
        Transaction tx = Build.A.Transaction.WithType(TxType.EIP1559).WithSenderAddress(TestItem.AddressA).TestObject;

        Assert.That(Accept(tx), Is.EqualTo(AcceptTxResult.Accepted));
    }

    private static AcceptTxResult Accept(Transaction tx)
    {
        FrameTxMisplacedExpiryFrameFilter filter = new(LimboLogs.Instance.GetClassLogger<FrameTxMisplacedExpiryFrameFilterTest>());
        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>(), Eip8141Prototype.Instance);
        return filter.Accept(tx, ref state, TxHandlingOptions.None);
    }
}
