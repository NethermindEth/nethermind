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
internal class FrameTxVerifyAfterPrefixFilterTest
{
    private static IEnumerable<TestCaseData> PrefixCases()
    {
        yield return new TestCaseData(new[] { SelfVerify(), Execution() }, AcceptTxResult.Accepted)
            .SetName("a self relay prefix followed by a user op is admissible");
        yield return new TestCaseData(new[] { Deploy(), OnlyVerify(), Pay(), Execution(), PostTx() }, AcceptTxResult.Accepted)
            .SetName("the longest recognized prefix followed by a body is admissible");
        yield return new TestCaseData(new[] { SelfVerify(), Execution(), PostTx() }, AcceptTxResult.Accepted)
            .SetName("a POST_TX frame behind the prefix is admissible");
        yield return new TestCaseData(new[] { Expiry(), SelfVerify(), Execution() }, AcceptTxResult.Accepted)
            .SetName("a leading expiry frame joins the prefix rather than ending it");
        yield return new TestCaseData(new[] { Expiry(), Deploy(), OnlyVerify(), Pay(), Execution() }, AcceptTxResult.Accepted)
            .SetName("a leading expiry frame ahead of the longest prefix is admissible");
        yield return new TestCaseData(new[] { SelfVerify(), Execution(), OnlyVerify() }, AcceptTxResult.FrameTxVerifyAfterPrefix)
            .SetName("a VERIFY frame behind a self relay prefix is rejected");
        yield return new TestCaseData(new[] { SelfVerify(), Pay() }, AcceptTxResult.FrameTxVerifyAfterPrefix)
            .SetName("a pay frame behind a self relay prefix is rejected");
        // Only reachable with this filter in isolation: through the pool the placement filter claims it first.
        yield return new TestCaseData(new[] { SelfVerify(), Expiry() }, AcceptTxResult.FrameTxVerifyAfterPrefix)
            .SetName("a trailing expiry frame is rejected as a VERIFY frame behind the prefix");
        yield return new TestCaseData(new[] { OnlyVerify(), Pay(), Execution(), Pay() }, AcceptTxResult.FrameTxVerifyAfterPrefix)
            .SetName("a VERIFY frame behind a paymaster prefix is rejected");
        // A layout matching none of the recognized prefixes has no boundary to sit behind; the rules
        // that reject it do so on their own terms.
        yield return new TestCaseData(new[] { Execution(), SelfVerify() }, AcceptTxResult.Accepted)
            .SetName("an unrecognized layout is left to the other rules");
    }

    [TestCaseSource(nameof(PrefixCases))]
    public void Accept_RejectsAVerifyFrameAfterTheValidationPrefix(TxFrame[] frames, AcceptTxResult expected)
    {
        long before = Metrics.PendingTransactionsFrameTxVerifyAfterPrefix;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Accept(FrameTx(frames)), Is.EqualTo(expected));
            Assert.That(Metrics.PendingTransactionsFrameTxVerifyAfterPrefix,
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
        FrameTxVerifyAfterPrefixFilter filter = new(LimboLogs.Instance.GetClassLogger<FrameTxVerifyAfterPrefixFilterTest>());
        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>(), Eip8141Prototype.Instance);
        return filter.Accept(tx, ref state, TxHandlingOptions.None);
    }
}
