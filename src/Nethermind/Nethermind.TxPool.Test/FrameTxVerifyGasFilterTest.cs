// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool.Filters;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.Core.Test.Builders.FrameTxTestFrames;

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

    private static TxFrame SelfVerifyWithState(ulong executionGasLimit, ulong stateGasLimit) =>
        new(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, executionGasLimit, stateGasLimit, UInt256.Zero, default);

    private static TxFrame ExecutionWithState(ulong executionGasLimit, ulong stateGasLimit) =>
        new(TxFrame.ModeSender, TxFrame.ApproveScopeNone, TestItem.AddressB, executionGasLimit, stateGasLimit, UInt256.Zero, default);

    private static IEnumerable<TestCaseData> StatePrefixCases()
    {
        yield return new TestCaseData(new[] { SelfVerifyWithState(1_000, 500_000) }, AcceptTxResult.Accepted)
            .SetName("prefix state exactly at MAX_VERIFY_STATE_GAS is accepted");
        yield return new TestCaseData(new[] { SelfVerifyWithState(1_000, 500_001) }, AcceptTxResult.FrameTxVerifyStateGasTooHigh)
            .SetName("prefix state one gas over MAX_VERIFY_STATE_GAS is rejected");
        yield return new TestCaseData(new[] { SelfVerify(1_000), ExecutionWithState(1_000, 3_000_000) }, AcceptTxResult.Accepted)
            .SetName("state behind a recognized prefix is outside the ceiling");
    }

    [TestCaseSource(nameof(StatePrefixCases))]
    public void Accept_BoundsThePrefixStateGas(TxFrame[] frames, AcceptTxResult expected)
    {
        Transaction tx = FrameTx(frames);
        FrameTxVerifyGasFilter filter = new(new TxPoolConfig { FrameTxMaxVerifyGas = 0, FrameTxMaxVerifyStateGas = 500_000 }, LimboLogs.Instance.GetClassLogger<FrameTxVerifyGasFilterTest>());
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
