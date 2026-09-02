// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs.Forks;
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
    // An unrecognized layout is charged its whole frame list: whether an approving DEFAULT frame approves at
    // all depends on sender-controlled code, so the frames behind it may still run before any gas is paid.
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
        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>(), Eip8141Prototype.Instance);

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
        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>(), Eip8141Prototype.Instance);

        Assert.That(filter.Accept(tx, ref state, TxHandlingOptions.None), Is.EqualTo(expected));
    }

    private static TxFrameSignature[] Signatures(int count, byte scheme)
    {
        TxFrameSignature[] signatures = new TxFrameSignature[count];
        for (int i = 0; i < count; i++)
        {
            signatures[i] = scheme switch
            {
                TxFrameSignature.SchemeSecp256k1 => Secp256k1Signature(TestItem.AddressA),
                TxFrameSignature.SchemeP256 => new TxFrameSignature(TxFrameSignature.SchemeP256, TestItem.AddressA, default, new byte[TxFrameSignature.P256SignatureLength]),
                _ => new TxFrameSignature(TxFrameSignature.SchemeArbitrary, null, default, default),
            };
        }

        return signatures;
    }

    private static readonly int Secp256k1AtCeiling = (int)(Eip8141Constants.MaxVerifyGas / Eip8141Constants.Secp256k1VerificationGasCost);
    private static readonly int P256AtCeiling = (int)(Eip8141Constants.MaxVerifyGas / Eip8141Constants.P256VerificationGasCost);
    private const int DecoderSignatureCap = 1024;

    private static IEnumerable<TestCaseData> LiftedCeilingCases()
    {
        yield return new TestCaseData(Secp256k1AtCeiling, TxFrameSignature.SchemeSecp256k1, AcceptTxResult.Accepted)
            .SetName("secp256k1 verification at the fixed MAX_VERIFY_GAS is accepted with the ceiling lifted");
        yield return new TestCaseData(Secp256k1AtCeiling + 1, TxFrameSignature.SchemeSecp256k1, AcceptTxResult.FrameTxVerifyGasTooHigh)
            .SetName("secp256k1 verification over the fixed MAX_VERIFY_GAS is rejected with the ceiling lifted");
        yield return new TestCaseData(P256AtCeiling, TxFrameSignature.SchemeP256, AcceptTxResult.Accepted)
            .SetName("P256 verification at the fixed MAX_VERIFY_GAS is accepted with the ceiling lifted");
        yield return new TestCaseData(P256AtCeiling + 1, TxFrameSignature.SchemeP256, AcceptTxResult.FrameTxVerifyGasTooHigh)
            .SetName("P256 verification over the fixed MAX_VERIFY_GAS is rejected with the ceiling lifted");
        yield return new TestCaseData(DecoderSignatureCap, TxFrameSignature.SchemeArbitrary, AcceptTxResult.Accepted)
            .SetName("a decoder-cap run of arbitrary entries stays under the fixed MAX_VERIFY_GAS with the ceiling lifted");
    }

    [TestCaseSource(nameof(LiftedCeilingCases))]
    public void Accept_BoundsSignatureVerificationGasAtTheFixedCeiling_WhenVerifyGasIsLifted(int signatureCount, byte scheme, AcceptTxResult expected)
    {
        Transaction tx = FrameTx(TestItem.AddressA, Signatures(signatureCount, scheme), Execution());
        FrameTxVerifyGasFilter filter = new(new TxPoolConfig { FrameTxMaxVerifyGas = 0, FrameTxMaxVerifyStateGas = 0 }, LimboLogs.Instance.GetClassLogger<FrameTxVerifyGasFilterTest>());
        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>(), Eip8141Prototype.Instance);

        Assert.That(filter.Accept(tx, ref state, TxHandlingOptions.None), Is.EqualTo(expected));
    }

    // The account cache stores the empty account on a miss while the reader beneath may leave the out-value
    // zeroed, so filters reading the first and second probe must not see a different sender.
    [Test]
    public void SenderAccount_OfAMissingAccount_ReadsTheSameOnEveryProbe()
    {
        TxFilteringState state = new(FrameTx(SelfVerify(1_000)), Substitute.For<IAccountStateProvider>(), Eip8141Prototype.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.SenderAccount.HasCode, Is.False);
            Assert.That(state.SenderAccount.IsTotallyEmpty, Is.True);
            Assert.That(state.SenderAccount.HasCode, Is.False);
        }
    }
}
