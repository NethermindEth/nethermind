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
    private const ulong ConfiguredMaxVerifyGas = 400_000;

    // An unrecognized layout is charged its whole frame list: whether an approving DEFAULT frame approves at
    // all depends on sender-controlled code, so the frames behind it may still run before any gas is paid.
    private static IEnumerable<TestCaseData> PrefixCases()
    {
        yield return new TestCaseData(new[] { SelfVerify(1_000), Execution(3_000_000) }, AcceptTxResult.Accepted, (ulong?)ConfiguredMaxVerifyGas)
            .SetName("execution behind a recognized prefix is outside the ceiling");
        yield return new TestCaseData(new[] { ApprovingDefault(1_000), Execution(3_000_000) }, AcceptTxResult.FrameTxVerifyGasTooHigh, (ulong?)ConfiguredMaxVerifyGas)
            .SetName("an unrecognized layout is charged its whole frame list");
        yield return new TestCaseData(new[] { ApprovingDefault(1_000), Execution(20_000) }, AcceptTxResult.Accepted, (ulong?)ConfiguredMaxVerifyGas)
            .SetName("an unrecognized layout under the ceiling is still accepted");
        yield return new TestCaseData(new[] { SelfVerify(1_000_000) }, AcceptTxResult.Accepted, (ulong?)0)
            .SetName("zero still disables the static prefix ceiling");
        // The bound is inclusive, as the fixed and state-gas ceilings below are.
        yield return new TestCaseData(new[] { ApprovingDefault(ConfiguredMaxVerifyGas) }, AcceptTxResult.Accepted, (ulong?)ConfiguredMaxVerifyGas)
            .SetName("a prefix exactly at the configured ceiling is accepted");
        yield return new TestCaseData(new[] { ApprovingDefault(ConfiguredMaxVerifyGas + 1) }, AcceptTxResult.FrameTxVerifyGasTooHigh, (ulong?)ConfiguredMaxVerifyGas)
            .SetName("a prefix one gas over the configured ceiling is rejected");
        yield return new TestCaseData(new[] { SelfVerify(235_800) }, AcceptTxResult.Accepted, null)
            .SetName("the node default admits the soispoke profile budget");
        yield return new TestCaseData(new[] { SelfVerify(300_000) }, AcceptTxResult.Accepted, null)
            .SetName("the node default admits work at the shared floor");
        yield return new TestCaseData(new[] { SelfVerify(300_001) }, AcceptTxResult.FrameTxVerifyGasTooHigh, null)
            .SetName("the node default rejects work above the shared floor");
        yield return new TestCaseData(new[] { SelfVerify(500_000) }, AcceptTxResult.Accepted, (ulong?)500_000)
            .SetName("raised headroom accepts its configured limit");
        yield return new TestCaseData(new[] { SelfVerify(500_001) }, AcceptTxResult.FrameTxVerifyGasTooHigh, (ulong?)500_000)
            .SetName("raised headroom rejects one gas above its configured limit");
    }

    [TestCaseSource(nameof(PrefixCases))]
    public void Accept_ChargesEveryFrameThatMayRunBeforePayment(TxFrame[] frames, AcceptTxResult expected, ulong? maxVerifyGas)
    {
        Transaction tx = FrameTx(frames);
        TxPoolConfig config = new();
        if (maxVerifyGas.HasValue) config.FrameTxMaxVerifyGas = maxVerifyGas.Value;
        FrameTxVerifyGasFilter filter = new(config, LimboLogs.Instance.GetClassLogger<FrameTxVerifyGasFilterTest>());
        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>(), Eip8141Prototype.Instance);

        Assert.That(filter.Accept(tx, ref state, TxHandlingOptions.None), Is.EqualTo(expected));
    }

    [Test]
    public void Accept_IncludesClaimVerificationInTheConfiguredBudget(
        [Values(300_000UL, 500_000UL)] ulong budget, [Values(0UL, 1UL)] ulong excessGas)
    {
        Transaction tx = FrameTx(TestItem.AddressA, [Secp256k1Claim(TestItem.AddressA)],
            SelfVerify(budget - Eip8141Constants.Secp256k1VerificationGasCost + excessGas));
        FrameTxVerifyGasFilter filter = new(new TxPoolConfig { FrameTxMaxVerifyGas = budget }, LimboLogs.Instance.GetClassLogger<FrameTxVerifyGasFilterTest>());
        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>(), Eip8141Prototype.Instance);

        Assert.That(filter.Accept(tx, ref state, TxHandlingOptions.None),
            Is.EqualTo(excessGas == 0 ? AcceptTxResult.Accepted : AcceptTxResult.FrameTxVerifyGasTooHigh));
    }

    [Test]
    public void Accept_PositiveBudgetCannotFallBelowTheSharedFloor(
        [Values(1UL, 100_000UL, 299_999UL, 300_000UL)] ulong configured, [Values(0UL, 1UL)] ulong excessGas)
    {
        Transaction tx = FrameTx(SelfVerify(300_000 + excessGas));
        FrameTxVerifyGasFilter filter = new(new TxPoolConfig { FrameTxMaxVerifyGas = configured }, LimboLogs.Instance.GetClassLogger<FrameTxVerifyGasFilterTest>());
        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>(), Eip8141Prototype.Instance);

        Assert.That(filter.Accept(tx, ref state, TxHandlingOptions.None),
            Is.EqualTo(excessGas == 0 ? AcceptTxResult.Accepted : AcceptTxResult.FrameTxVerifyGasTooHigh));
    }

    private static TxFrame SelfVerifyWithState(ulong executionGasLimit, ulong stateGasLimit) =>
        new(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, target: null, executionGasLimit, stateGasLimit, UInt256.Zero, default);

    private static TxFrame ExecutionWithState(ulong executionGasLimit, ulong stateGasLimit) =>
        new(FrameMode.Sender, FrameFlags.None, TestItem.AddressB, executionGasLimit, stateGasLimit, UInt256.Zero, default);

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

    private static TxFrameClaim[] Claims(int count, Address verifier)
    {
        TxFrameClaim[] claims = new TxFrameClaim[count];
        for (int i = 0; i < count; i++)
        {
            claims[i] = new TxFrameClaim(verifier, TestItem.AddressA.Bytes.ToArray(), default, 0, 0);
        }

        return claims;
    }

    private static readonly int LeanSphincsAtCeiling = (int)(TxPoolConfig.DefaultFrameTxMaxVerifyGas / Eip8141Constants.LeanSphincsVerificationGasCost);
    private static readonly int P256AtCeiling = (int)(TxPoolConfig.DefaultFrameTxMaxVerifyGas / Eip8141Constants.P256VerificationGasCost);

    private static IEnumerable<TestCaseData> LiftedCeilingCases()
    {
        // MAX_CLAIMS bounds the list, so only the costlier verifiers can reach the fallback.
        yield return new TestCaseData(LeanSphincsAtCeiling, Eip8141Constants.LeanSphincsVerifierAddress, AcceptTxResult.Accepted)
            .SetName("leanSPHINCS verification at the default fallback is accepted with the ceiling lifted");
        yield return new TestCaseData(LeanSphincsAtCeiling + 1, Eip8141Constants.LeanSphincsVerifierAddress, AcceptTxResult.FrameTxVerifyGasTooHigh)
            .SetName("leanSPHINCS verification over the default fallback is rejected with the ceiling lifted");
        yield return new TestCaseData(P256AtCeiling, Eip8141Constants.P256VerifierAddress, AcceptTxResult.Accepted)
            .SetName("P256 verification at the default fallback is accepted with the ceiling lifted");
        yield return new TestCaseData(Eip8141Constants.MaxClaims, Eip8141Constants.Secp256k1VerifierAddress, AcceptTxResult.Accepted)
            .SetName("a full list of secp256k1 claims fits the default fallback");
    }

    [TestCaseSource(nameof(LiftedCeilingCases))]
    public void Accept_BoundsClaimVerificationGasAtTheDefaultFallback_WhenVerifyGasIsLifted(int claimCount, Address verifier, AcceptTxResult expected)
    {
        Transaction tx = FrameTx(TestItem.AddressA, Claims(claimCount, verifier), Execution());
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
