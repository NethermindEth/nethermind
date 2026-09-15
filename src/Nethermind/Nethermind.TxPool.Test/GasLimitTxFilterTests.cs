// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.TxPool.Filters;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

/// <summary>The pre-hash block-gas gate, which prices an EIP-8141 frame transaction's block reservations.</summary>
public class GasLimitTxFilterTests
{
    private const ulong OrdinaryGasLimit = 21_000;

    // EIP-2780 charges a value-bearing frame, so the two specs price one transaction differently and the
    // verdict says which was used. The pinned one is the whole submission's, however the head has since moved.
    [TestCase(true, false, TestName = "the pinned spec is the charging one")]
    [TestCase(false, true, TestName = "the head has moved to the charging one")]
    public void Accept_FrameTx_JudgesItAgainstTheSpecPinnedForTheSubmission(bool pinnedCharges, bool headCharges)
    {
        ReleaseSpec lenient = SpecCharging(false);
        Assert.That(FrameTxValidation.TryCalculateBlockGasReservations(ValueBearingFrameTx(), lenient, out ulong affordable, out _), Is.True);
        Assert.That(FrameTxValidation.TryCalculateBlockGasReservations(ValueBearingFrameTx(), SpecCharging(true), out ulong charged, out _), Is.True);
        Assert.That(charged, Is.GreaterThan(affordable), "the two specs must disagree, or this pins nothing");

        // Exactly the lenient reservation: the charging spec is the only one that overshoots it.
        AcceptTxResult result = Accept(ValueBearingFrameTx(), affordable, SpecCharging(pinnedCharges), SpecCharging(headCharges));

        Assert.That(result, Is.EqualTo(pinnedCharges ? AcceptTxResult.GasLimitExceeded : AcceptTxResult.Accepted));
    }

    [Test]
    public void Accept_OrdinaryTx_ReadsNoSpecAtAll()
    {
        // Every pre-fork transaction walks this filter, and only a frame transaction has a budget to price.
        IChainHeadSpecProvider specProvider = Substitute.For<IChainHeadSpecProvider>();
        Transaction tx = Build.A.Transaction.WithGasLimit(OrdinaryGasLimit).TestObject;

        AcceptTxResult result = Accept(tx, OrdinaryGasLimit, SpecCharging(false), specProvider);

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
        specProvider.DidNotReceive().GetCurrentHeadSpec();
    }

    // Negative control: the ordinary leg reads no spec either way, so its verdict is unmoved by all of the above.
    [TestCase(OrdinaryGasLimit, false, TestName = "an ordinary tx at the block gas limit")]
    [TestCase(OrdinaryGasLimit - 1, true, TestName = "an ordinary tx over the block gas limit")]
    public void Accept_OrdinaryTx_GatesOnTheBlockGasLimit(ulong blockGasLimit, bool rejected)
    {
        Transaction tx = Build.A.Transaction.WithGasLimit(OrdinaryGasLimit).TestObject;

        AcceptTxResult result = Accept(tx, blockGasLimit, SpecCharging(false), SpecCharging(true));

        Assert.That(result, Is.EqualTo(rejected ? AcceptTxResult.GasLimitExceeded : AcceptTxResult.Accepted));
    }

    private static ReleaseSpec SpecCharging(bool valueTransfers) => new() { IsEip2780Enabled = valueTransfers };

    /// <summary>A frame transaction whose SENDER frame moves wei to a third party, which EIP-2780 prices.</summary>
    private static Transaction ValueBearingFrameTx() => FrameTxTestFrames.FrameTx(
        FrameTxTestFrames.SelfVerify(),
        new TxFrame(TxFrame.ModeSender, TxFrame.ApproveScopeNone, TestItem.AddressC, OrdinaryGasLimit, UInt256.One, default));

    private static AcceptTxResult Accept(Transaction tx, ulong blockGasLimit, IReleaseSpec pinnedSpec, IReleaseSpec headSpec)
    {
        IChainHeadSpecProvider specProvider = Substitute.For<IChainHeadSpecProvider>();
        specProvider.GetCurrentHeadSpec().Returns(headSpec);
        return Accept(tx, blockGasLimit, pinnedSpec, specProvider);
    }

    private static AcceptTxResult Accept(Transaction tx, ulong blockGasLimit, IReleaseSpec pinnedSpec, IChainHeadSpecProvider specProvider)
    {
        IChainHeadInfoProvider headInfo = Substitute.For<IChainHeadInfoProvider>();
        headInfo.BlockGasLimit.Returns(blockGasLimit);
        headInfo.SpecProvider.Returns(specProvider);

        GasLimitTxFilter filter = new(headInfo, new TxPoolConfig(), LimboLogs.Instance);
        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>(), pinnedSpec);
        return filter.Accept(tx, ref state, TxHandlingOptions.None);
    }
}
