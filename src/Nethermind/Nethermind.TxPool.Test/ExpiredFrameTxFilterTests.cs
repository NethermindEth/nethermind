// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.TxPool.Filters;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.Core.Test.Builders.FrameTxTestFrames;

namespace Nethermind.TxPool.Test;

// The cases assert against the shared rejection counter.
[NonParallelizable]
internal class ExpiredFrameTxFilterTests
{
    private const ulong HeadTimestamp = 1_500;

    private static IEnumerable<TestCaseData> DeadlineCases()
    {
        yield return new TestCaseData(HeadTimestamp - 1, AcceptTxResult.FrameTxExpired)
            .SetName("a deadline one second behind the head is rejected");
        yield return new TestCaseData(HeadTimestamp, AcceptTxResult.Accepted)
            .SetName("a deadline equal to the head timestamp is still admissible");
        yield return new TestCaseData(HeadTimestamp + 1, AcceptTxResult.Accepted)
            .SetName("a deadline one second ahead of the head is admissible");
        yield return new TestCaseData(0UL, AcceptTxResult.FrameTxExpired)
            .SetName("a zero deadline is a deadline, not the absent-deadline encoding");
    }

    // The predeploy reverts only once the head timestamp is strictly past the deadline, so the boundary
    // second belongs to the transaction.
    [TestCaseSource(nameof(DeadlineCases))]
    public void Accept_TreatsTheDeadlineAsInclusive(ulong deadline, AcceptTxResult expected)
    {
        long before = Metrics.PendingTransactionsFrameTxExpired;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Accept(FrameTx(ExpiryAt(deadline), SelfVerify())), Is.EqualTo(expected));
            Assert.That(Metrics.PendingTransactionsFrameTxExpired,
                Is.EqualTo(expected == AcceptTxResult.Accepted ? before : before + 1));
        }
    }

    [Test]
    public void Accept_LeavesAFrameTransactionWithoutAnExpiryFrameAlone()
    {
        long before = Metrics.PendingTransactionsFrameTxExpired;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Accept(FrameTx(SelfVerify(), Execution())), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(Metrics.PendingTransactionsFrameTxExpired, Is.EqualTo(before));
        }
    }

    // The deadline is read from the leading frame alone, which is what the placement filter ahead of this one
    // guarantees; a trailing expiry frame is that filter's rejection, never a silent pass on an elapsed deadline.
    [Test]
    public void Accept_ReadsTheDeadlineFromTheLeadingFrameOnly()
    {
        long before = Metrics.PendingTransactionsFrameTxExpired;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Accept(FrameTx(SelfVerify(), ExpiryAt(HeadTimestamp - 1))), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(Metrics.PendingTransactionsFrameTxExpired, Is.EqualTo(before));
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
        IChainHeadInfoProvider headInfo = Substitute.For<IChainHeadInfoProvider>();
        headInfo.HeadTimestamp.Returns(HeadTimestamp);

        ExpiredFrameTxFilter filter = new(headInfo, LimboLogs.Instance.GetClassLogger<ExpiredFrameTxFilterTests>());
        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>(), Eip8141Prototype.Instance);
        return filter.Accept(tx, ref state, TxHandlingOptions.None);
    }
}
