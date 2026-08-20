// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.TxPool.Filters;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.TxPool.Test.FrameTxTestFrames;

namespace Nethermind.TxPool.Test;

[Parallelizable(ParallelScope.All)]
internal class FrameTxVerifyGasFilterTest
{
    // An unrecognized layout is charged its whole frame list: whether an approving DEFAULT frame approves
    // at all depends on code the sender controls, so the frames behind it may still run before any gas is
    // paid. A layout that fits under the ceiling anyway costs the node no more than a recognized one.
    private static IEnumerable<TestCaseData> PrefixCases()
    {
        yield return new TestCaseData(new[] { SelfVerify(1_000), Execution(3_000_000) }, AcceptTxResult.Accepted)
            .SetName("execution behind a recognized prefix is outside the ceiling");
        yield return new TestCaseData(new[] { ApprovingDefault(1_000), Execution(3_000_000) }, AcceptTxResult.FrameTxVerifyGasTooHigh)
            .SetName("an unrecognized layout is charged its whole frame list");
        yield return new TestCaseData(new[] { ApprovingDefault(1_000), Execution(20_000) }, AcceptTxResult.Accepted)
            .SetName("an unrecognized layout under the ceiling is still accepted");
    }

    [TestCaseSource(nameof(PrefixCases))]
    public void Accept_ChargesEveryFrameThatMayRunBeforePayment(TxFrame[] frames, AcceptTxResult expected)
    {
        Transaction tx = FrameTx(frames);
        FrameTxVerifyGasFilter filter = new(new TxPoolConfig { FrameTxMaxVerifyGas = 100_000 }, LimboLogs.Instance.GetClassLogger<FrameTxVerifyGasFilterTest>());
        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>());

        Assert.That(filter.Accept(tx, ref state, TxHandlingOptions.None), Is.EqualTo(expected));
    }

    // The pool's account cache stores the empty account on a miss while the reader beneath it may
    // leave the out-value zeroed, so a filter reading the first probe and a filter reading the
    // second one must not see a different sender.
    [Test]
    public void SenderAccount_OfAMissingAccount_ReadsTheSameOnEveryProbe()
    {
        TxFilteringState state = new(FrameTx(SelfVerify(1_000)), Substitute.For<IAccountStateProvider>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.SenderAccount.HasCode, Is.False);
            Assert.That(state.SenderAccount.IsTotallyEmpty, Is.True);
            Assert.That(state.SenderAccount.HasCode, Is.False);
        }
    }
}
