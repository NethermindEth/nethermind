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

namespace Nethermind.TxPool.Test;

[Parallelizable(ParallelScope.All)]
internal class FrameTxVerifyGasFilterTest
{
    private static Transaction FrameTx(params TxFrame[] frames) => new()
    {
        Type = TxType.FrameTx,
        SenderAddress = TestItem.AddressA,
        Frames = frames,
        FrameSignatures = [],
    };

    private static TxFrame SelfVerify(ulong gasLimit) =>
        new(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit, UInt256.Zero, default);

    private static TxFrame Execution(ulong gasLimit) =>
        new(TxFrame.ModeSender, TxFrame.ApproveScopeNone, TestItem.AddressB, gasLimit, UInt256.Zero, default);

    private static TxFrame ApprovingDefault(ulong gasLimit) =>
        new(TxFrame.ModeDefault, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit, UInt256.Zero, default);

    // Approving flags on a DEFAULT frame do not end the validation prefix: whether that frame approves
    // at all depends on code the sender controls, so the frames behind it may still run before any gas
    // is paid and are charged too. That is the ceiling bypass a sender on a delegation had; a layout
    // whose whole frame list fits under the ceiling costs the node no more than a recognized one and
    // stays admissible.
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
