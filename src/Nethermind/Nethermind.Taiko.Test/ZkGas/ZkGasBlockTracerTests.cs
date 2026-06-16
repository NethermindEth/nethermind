// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Taiko.ZkGas;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Taiko.Test.ZkGas;

/// <summary>
/// Unit tests for <see cref="ZkGasBlockTracer"/>, covering:
/// <list type="bullet">
///   <item>StartNewBlockTrace resets the meter and publishes it to any holder.</item>
///   <item>StartNewTxTrace calls ResetTransaction on the meter.</item>
///   <item>EndTxTrace commits the transaction ZK gas into the block total.</item>
///   <item>EndTxTrace is NOT called for invalid transactions; the in-flight gas is
///       discarded by the subsequent StartNewTxTrace via ResetTransaction.</item>
///   <item>The inner tracer receives all forwarded callbacks.</item>
/// </list>
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class ZkGasBlockTracerTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>Creates a stub block for use in StartNewBlockTrace.</summary>
    private static Block MakeBlock() =>
        Build.A.Block.WithNumber(1).TestObject;

    /// <summary>Creates a stub transaction for use in StartNewTxTrace.</summary>
    private static Transaction MakeTx() =>
        Build.A.Transaction.TestObject;

    /// <summary>
    /// Constructs a tracer with <see cref="ZkGasTestSchedules"/> wired in so opcode-specific
    /// assertions (e.g. CREATE=1, ADD=19) resolve against known multipliers.
    /// </summary>
    private static ZkGasBlockTracer MakeTracer(IBlockTracer inner, ZkGasMeterHolder? holder = null,
        ulong blockZkGasLimit = ZkGasSchedule.BlockZkGasLimit,
        ulong txIntrinsicZkGas = ZkGasSchedule.TxIntrinsicZkGas) =>
        new(inner, holder, blockZkGasLimit, txIntrinsicZkGas,
            ZkGasTestSchedules.OpcodeMultipliers,
            ZkGasTestSchedules.PrecompileMultipliers);

    // ── meter reset / holder publishing ──────────────────────────────────────

    /// <summary>
    /// StartNewBlockTrace creates a fresh meter and publishes it to the holder.
    /// </summary>
    [Test]
    public void StartNewBlockTrace_ResetsMeter_AndPublishesToHolder()
    {
        ZkGasMeterHolder holder = new();
        IBlockTracer inner = Substitute.For<IBlockTracer>();
        ZkGasBlockTracer blockTracer = MakeTracer(inner, holder);

        blockTracer.StartNewBlockTrace(MakeBlock());

        Assert.That(holder.Meter, Is.Not.Null);
        Assert.That(holder.Meter, Is.SameAs(blockTracer.Meter));
        Assert.That(blockTracer.Meter.BlockZkGasUsed, Is.EqualTo(0UL));
    }

    /// <summary>
    /// A second call to StartNewBlockTrace reuses the same meter instance (the holder
    /// was already pointed at it during construction) but resets its per-block accounting.
    /// </summary>
    [Test]
    public void StartNewBlockTrace_SecondCall_ResetsMeter()
    {
        ZkGasMeterHolder holder = new();
        IBlockTracer inner = Substitute.For<IBlockTracer>();
        ZkGasBlockTracer blockTracer = MakeTracer(inner, holder);

        blockTracer.StartNewBlockTrace(MakeBlock());
        ZkGasMeter firstMeter = blockTracer.Meter;

        // Accrue some block-level gas, then start a new block.
        blockTracer.Meter.ChargeOpcode(0x01, 1_000);
        blockTracer.Meter.CommitTransaction();
        Assert.That(blockTracer.Meter.BlockZkGasUsed, Is.GreaterThan(0UL));

        blockTracer.StartNewBlockTrace(MakeBlock());

        Assert.That(blockTracer.Meter, Is.SameAs(firstMeter), "meter instance must be reused across blocks");
        Assert.That(holder.Meter, Is.SameAs(blockTracer.Meter));
        Assert.That(blockTracer.Meter.BlockZkGasUsed, Is.EqualTo(0UL), "ResetBlock must clear accrued block ZK gas");
        Assert.That(blockTracer.Meter.IsLimitExceeded, Is.False);
    }

    // ── tx-level commit / reset ───────────────────────────────────────────────

    /// <summary>
    /// After StartNewTxTrace the in-flight ZK gas is zero (ResetTransaction was called).
    /// </summary>
    [Test]
    public void StartNewTxTrace_ResetsInFlightGas()
    {
        IBlockTracer inner = Substitute.For<IBlockTracer>();
        inner.StartNewTxTrace(Arg.Any<Transaction?>()).Returns(NullTxTracer.Instance);
        // txIntrinsicZkGas:0 isolates the reset-behavior assertion from the intrinsic charge.
        ZkGasBlockTracer blockTracer = MakeTracer(inner, txIntrinsicZkGas: 0);

        blockTracer.StartNewBlockTrace(MakeBlock());

        // Simulate leftover in-flight gas from a previous failed tx
        blockTracer.Meter.ChargeOpcode(0x01, 999_999);

        // Starting a new trace must reset the in-flight amount
        blockTracer.StartNewTxTrace(MakeTx());

        Assert.That(blockTracer.Meter.TxZkGasUsed, Is.EqualTo(0UL));
    }

    /// <summary>
    /// EndTxTrace commits the in-flight gas into the block total.
    /// </summary>
    [Test]
    public void EndTxTrace_CommitsInFlightGas_IntoBlockTotal()
    {
        IBlockTracer inner = Substitute.For<IBlockTracer>();
        inner.StartNewTxTrace(Arg.Any<Transaction?>()).Returns(NullTxTracer.Instance);
        ZkGasBlockTracer blockTracer = MakeTracer(inner);

        blockTracer.StartNewBlockTrace(MakeBlock());
        blockTracer.StartNewTxTrace(MakeTx());
        blockTracer.Meter.ChargeOpcode(0x01, 10); // charge some ZK gas
        ulong inFlight = blockTracer.Meter.TxZkGasUsed;

        blockTracer.EndTxTrace();

        Assert.That(blockTracer.Meter.TxZkGasUsed, Is.EqualTo(0UL));
        Assert.That(blockTracer.Meter.BlockZkGasUsed, Is.EqualTo(inFlight));
    }

    /// <summary>
    /// When a transaction is invalid and EndTxTrace is NOT called, the in-flight ZK gas
    /// is discarded by the next StartNewTxTrace (ResetTransaction). This is the path
    /// taken by BlockInvalidTxExecutor for failed transactions.
    /// </summary>
    [Test]
    public void InvalidTx_InFlightGas_DiscardedByNextStartNewTxTrace()
    {
        IBlockTracer inner = Substitute.For<IBlockTracer>();
        inner.StartNewTxTrace(Arg.Any<Transaction?>()).Returns(NullTxTracer.Instance);
        // txIntrinsicZkGas:0 isolates the discard-behavior assertion from the intrinsic charge.
        ZkGasBlockTracer blockTracer = MakeTracer(inner, txIntrinsicZkGas: 0);

        blockTracer.StartNewBlockTrace(MakeBlock());

        // Tx 1: charges gas but is then skipped (invalid) — EndTxTrace never called
        blockTracer.StartNewTxTrace(MakeTx());
        blockTracer.Meter.ChargeOpcode(0x20, 100); // keccak256 – some charge
        ulong afterInvalidTx = blockTracer.Meter.BlockZkGasUsed;

        // Tx 2: starts — this calls ResetTransaction, discarding the in-flight gas
        blockTracer.StartNewTxTrace(MakeTx());

        Assert.That(blockTracer.Meter.TxZkGasUsed, Is.EqualTo(0UL),
            "In-flight gas from the invalid tx must be discarded");
        Assert.That(blockTracer.Meter.BlockZkGasUsed, Is.EqualTo(afterInvalidTx),
            "Block total must not include gas from the invalid tx");
    }

    /// <summary>
    /// Multiple committed transactions accumulate in the block total correctly.
    /// </summary>
    [Test]
    public void MultipleCommits_AccumulateBlockTotal()
    {
        IBlockTracer inner = Substitute.For<IBlockTracer>();
        inner.StartNewTxTrace(Arg.Any<Transaction?>()).Returns(NullTxTracer.Instance);
        ZkGasBlockTracer blockTracer = MakeTracer(inner);

        blockTracer.StartNewBlockTrace(MakeBlock());

        ulong total = 0;
        for (int i = 0; i < 3; i++)
        {
            blockTracer.StartNewTxTrace(MakeTx());
            blockTracer.Meter.ChargeOpcode(0x01, 5); // ADD, some charge
            total += blockTracer.Meter.TxZkGasUsed;
            blockTracer.EndTxTrace();
        }

        Assert.That(blockTracer.Meter.BlockZkGasUsed, Is.EqualTo(total));
    }

    // ── delegation to inner tracer ────────────────────────────────────────────

    /// <summary>
    /// IsTracingRewards is delegated to the inner tracer.
    /// </summary>
    [Test]
    public void IsTracingRewards_DelegatedToInner()
    {
        IBlockTracer inner = Substitute.For<IBlockTracer>();
        inner.IsTracingRewards.Returns(true);
        ZkGasBlockTracer blockTracer = MakeTracer(inner);

        Assert.That(blockTracer.IsTracingRewards, Is.True);
    }

    /// <summary>
    /// ReportReward is forwarded to the inner tracer.
    /// </summary>
    [Test]
    public void ReportReward_ForwardedToInner()
    {
        IBlockTracer inner = Substitute.For<IBlockTracer>();
        ZkGasBlockTracer blockTracer = MakeTracer(inner);

        blockTracer.ReportReward(TestItem.AddressA, "block", 1);

        inner.Received(1).ReportReward(TestItem.AddressA, "block", (UInt256)1);
    }

    // ── tx intrinsic ZK gas ───────────────────────────────────────────────────

    /// <summary>
    /// StartNewTxTrace charges the flat per-transaction intrinsic ZK gas immediately
    /// after resetting in-flight gas, before any opcode can run.
    /// </summary>
    [Test]
    public void StartNewTxTrace_ChargesIntrinsicBeforeTracing()
    {
        IBlockTracer inner = Substitute.For<IBlockTracer>();
        inner.StartNewTxTrace(Arg.Any<Transaction?>()).Returns(NullTxTracer.Instance);
        // Explicitly set a known intrinsic to decouple from the schedule constant.
        const ulong intrinsic = 243_000UL;
        ZkGasBlockTracer blockTracer = MakeTracer(inner, txIntrinsicZkGas: intrinsic);

        blockTracer.StartNewBlockTrace(MakeBlock());
        blockTracer.StartNewTxTrace(MakeTx());

        Assert.That(blockTracer.Meter.TxZkGasUsed, Is.EqualTo(intrinsic),
            "Intrinsic charge must appear in TxZkGasUsed immediately after StartNewTxTrace");
    }

    /// <summary>
    /// When txIntrinsicZkGas is 0, StartNewTxTrace leaves TxZkGasUsed at zero.
    /// </summary>
    [Test]
    public void StartNewTxTrace_IntrinsicIsZero_LeavesTxGasAtZero()
    {
        IBlockTracer inner = Substitute.For<IBlockTracer>();
        inner.StartNewTxTrace(Arg.Any<Transaction?>()).Returns(NullTxTracer.Instance);
        ZkGasBlockTracer blockTracer = MakeTracer(inner, txIntrinsicZkGas: 0);

        blockTracer.StartNewBlockTrace(MakeBlock());
        blockTracer.StartNewTxTrace(MakeTx());

        Assert.That(blockTracer.Meter.TxZkGasUsed, Is.EqualTo(0UL),
            "Zero intrinsic must leave TxZkGasUsed at zero");
    }

    /// <summary>
    /// When the intrinsic alone would push the projected block total past the limit,
    /// IsLimitExceeded is set immediately by StartNewTxTrace.
    /// </summary>
    [Test]
    public void StartNewTxTrace_IntrinsicAlone_SetsLimitExceeded_WhenBudgetTooSmall()
    {
        IBlockTracer inner = Substitute.For<IBlockTracer>();
        inner.StartNewTxTrace(Arg.Any<Transaction?>()).Returns(NullTxTracer.Instance);
        const ulong intrinsic = 243_000UL;
        const ulong tinyLimit = intrinsic - 1; // one under the intrinsic cost
        ZkGasBlockTracer blockTracer = MakeTracer(inner, blockZkGasLimit: tinyLimit, txIntrinsicZkGas: intrinsic);

        blockTracer.StartNewBlockTrace(MakeBlock());
        blockTracer.StartNewTxTrace(MakeTx());

        Assert.That(blockTracer.Meter.IsLimitExceeded, Is.True,
            "Intrinsic that exceeds the block budget must set IsLimitExceeded");
    }

    // ── validation regression: IsLimitExceeded must survive EndTxTrace ────────

    [Test]
    public void EndTxTrace_PreservesLimitExceeded_WhenChargeFailedMidTx()
    {
        IBlockTracer inner = Substitute.For<IBlockTracer>();
        inner.StartNewTxTrace(Arg.Any<Transaction?>()).Returns(NullTxTracer.Instance);
        ZkGasBlockTracer blockTracer = MakeTracer(inner, blockZkGasLimit: 1000, txIntrinsicZkGas: 0);

        blockTracer.StartNewBlockTrace(MakeBlock());
        blockTracer.StartNewTxTrace(MakeTx());

        blockTracer.Meter.ChargeOpcode(0xf0, 800); // succeeds: 800 in-flight
        blockTracer.Meter.ChargeOpcode(0xf0, 300); // fails: 800+300 > 1000, _txZkGasUsed stays at 800
        Assert.That(blockTracer.Meter.IsLimitExceeded, Is.True);

        // EndTxTrace must not commit the partial 800-unit total and clear the flag.
        blockTracer.EndTxTrace();

        Assert.That(blockTracer.Meter.IsLimitExceeded, Is.True);
        Assert.That(blockTracer.Meter.BlockZkGasUsed, Is.EqualTo(0UL));
    }

    [Test]
    public void EndTxTrace_PreservesLimitExceeded_WhenIntrinsicFailed()
    {
        IBlockTracer inner = Substitute.For<IBlockTracer>();
        inner.StartNewTxTrace(Arg.Any<Transaction?>()).Returns(NullTxTracer.Instance);
        // Limit=800, intrinsic=500. Pre-fill 600 units so only 200 budget remains.
        ZkGasBlockTracer blockTracer = MakeTracer(inner, blockZkGasLimit: 800, txIntrinsicZkGas: 500);

        blockTracer.StartNewBlockTrace(MakeBlock());

        // Pre-fill block to 600 via direct meter manipulation (bypasses intrinsic charge).
        blockTracer.Meter.ChargeOpcode(0xf0, 600);
        blockTracer.Meter.CommitTransaction(); // blockZkGasUsed = 600; 200 budget left

        // Second tx: intrinsic (500) > remaining budget (200) → StartNewTxTrace sets the flag.
        blockTracer.StartNewTxTrace(MakeTx());
        Assert.That(blockTracer.Meter.IsLimitExceeded, Is.True);

        blockTracer.EndTxTrace();

        Assert.That(blockTracer.Meter.IsLimitExceeded, Is.True);
        Assert.That(blockTracer.Meter.BlockZkGasUsed, Is.EqualTo(600UL));
    }
}
