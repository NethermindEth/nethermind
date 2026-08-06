// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

    // Approving flags on a DEFAULT frame do not end the validation prefix: whether that frame approves
    // at all depends on code the sender controls, so a prefix walk that trusted the flags would leave
    // the frames behind it unbudgeted while the node still runs them before any gas is paid.
    [TestCase(false, TestName = "a recognized prefix inside the ceiling is accepted")]
    [TestCase(true, TestName = "an unrecognized prefix is rejected whatever its gas")]
    public void Accept_RejectsAnUnrecognizedValidationPrefix(bool unrecognized)
    {
        Transaction tx = unrecognized
            ? FrameTx(
                new TxFrame(TxFrame.ModeDefault, TxFrame.ApproveExecutionAndPayment, target: null, 1_000, UInt256.Zero, default),
                Execution(3_000_000))
            : FrameTx(SelfVerify(1_000), Execution(3_000_000));

        FrameTxVerifyGasFilter filter = new(new TxPoolConfig { FrameTxMaxVerifyGas = 100_000 }, LimboLogs.Instance.GetClassLogger<FrameTxVerifyGasFilterTest>());
        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>());

        AcceptTxResult result = filter.Accept(tx, ref state, TxHandlingOptions.None);

        Assert.That(result, Is.EqualTo(unrecognized ? AcceptTxResult.FrameTxVerifyGasTooHigh : AcceptTxResult.Accepted));
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
