// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Taiko.ZkGas;
using NUnit.Framework;

namespace Nethermind.Taiko.Test.ZkGas;

/// <summary>
/// Unit tests for <see cref="ZkGasTxTracer"/>, covering:
/// <list type="bullet">
///   <item>Non-spawn opcodes: immediate metering via ChargeOpcode.</item>
///   <item>Spawn opcodes (deferred state machine): gas charged at ReportAction / ReportActionEnd
///       using fixed spawn estimate when child work is dispatched.</item>
///   <item>Deferred step flushed without spawn estimate when no ReportAction follows.</item>
///   <item>ReportActionError charges full forwarded gas (gasRemaining = 0).</item>
///   <item>ReportActionEnd / ReportActionRevert flush the deferred step and charge precompile.</item>
/// </list>
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class ZkGasTxTracerTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static (ZkGasTxTracer tracer, ZkGasMeter meter) Make()
    {
        ZkGasMeter meter = new();
        ZkGasTxTracer tracer = new(meter);
        return (tracer!, meter);
    }

    /// <summary>Returns a null <see cref="ExecutionEnvironment"/> suitable for tests that do not inspect it.</summary>
    private static ExecutionEnvironment Env() => null!;

    // ── non-spawn opcode ──────────────────────────────────────────────────────

    /// <summary>
    /// A non-spawn opcode (ADD = 0x01) is charged immediately after
    /// <see cref="ZkGasTxTracer.ReportOperationRemainingGas"/>.
    /// </summary>
    [Test]
    public void NonSpawnOpcode_ChargedImmediately()
    {
        (ZkGasTxTracer tracer, ZkGasMeter meter) = Make();

        // ADD = 0x01, multiplier = ZkGasSchedule.OpcodeMultipliers[0x01]
        tracer.StartOperation(0, Instruction.ADD, gas: 100, env: Env());
        tracer.ReportOperationRemainingGas(97); // 3 raw gas

        ulong expected = 3UL * ZkGasSchedule.OpcodeMultipliers[0x01];
        Assert.That(meter.TxZkGasUsed, Is.EqualTo(expected));
    }

    /// <summary>
    /// A second non-spawn opcode accumulates on top of the first.
    /// </summary>
    [Test]
    public void NonSpawnOpcode_Accumulates()
    {
        (ZkGasTxTracer tracer, ZkGasMeter meter) = Make();

        tracer.StartOperation(0, Instruction.ADD, gas: 100, env: Env());
        tracer.ReportOperationRemainingGas(97); // 3 raw

        tracer.StartOperation(1, Instruction.MUL, gas: 97, env: Env());
        tracer.ReportOperationRemainingGas(94); // 3 raw

        ulong expected = 3UL * ZkGasSchedule.OpcodeMultipliers[0x02]   // MUL = 0x02
                       + 3UL * ZkGasSchedule.OpcodeMultipliers[0x01];  // ADD = 0x01
        Assert.That(meter.TxZkGasUsed, Is.EqualTo(expected));
    }

    // ── spawn opcode — deferred, spawned ─────────────────────────────────────

    /// <summary>
    /// A CALL opcode followed by <see cref="ZkGasTxTracer.ReportAction"/> (child frame
    /// dispatched) must be charged at the fixed spawn estimate, not the raw gas delta.
    /// The deferred charge should be applied when the next opcode starts.
    /// </summary>
    [Test]
    public void SpawnOpcode_Deferred_UsesSpawnEstimate_WhenChildDispatched()
    {
        (ZkGasTxTracer tracer, ZkGasMeter meter) = Make();

        // CALL = 0xf1
        tracer.StartOperation(0, Instruction.CALL, gas: 50_000, env: Env());
        tracer.ReportOperationRemainingGas(25_000); // 25_000 raw – should be ignored
        // ReportAction signals that child work was dispatched
        tracer.ReportAction(25_000, UInt256.Zero, TestItem.AddressA, TestItem.AddressB,
            ReadOnlyMemory<byte>.Empty, ExecutionType.CALL);
        // Deferred step is flushed when the next opcode starts
        tracer.StartOperation(1, Instruction.STOP, gas: 0, env: Env());
        tracer.ReportOperationRemainingGas(0);

        // ChargeOpcode multiplies rawGas by OpcodeMultipliers[opcode], so spawn estimate is also multiplied
        ulong spawnCharge = ZkGasSchedule.SpawnEstimateCall * ZkGasSchedule.OpcodeMultipliers[0xf1];
        ulong stopCharge = 0; // STOP raw=0 → 0
        Assert.That(meter.TxZkGasUsed, Is.EqualTo(spawnCharge + stopCharge));
    }

    /// <summary>
    /// A CALL opcode where no <see cref="ZkGasTxTracer.ReportAction"/> follows
    /// (e.g. call to empty account that is not dispatched as a child frame) must use
    /// the raw measured gas delta, not the spawn estimate.
    /// </summary>
    [Test]
    public void SpawnOpcode_Deferred_UsesRawGas_WhenNoChildDispatched()
    {
        (ZkGasTxTracer tracer, ZkGasMeter meter) = Make();

        tracer.StartOperation(0, Instruction.CALL, gas: 50_000, env: Env());
        tracer.ReportOperationRemainingGas(47_000); // 3_000 raw
        // No ReportAction — child was not dispatched
        // Deferred step flushed by next opcode start
        tracer.StartOperation(1, Instruction.STOP, gas: 0, env: Env());
        tracer.ReportOperationRemainingGas(0);

        ulong expected = 3_000UL * ZkGasSchedule.OpcodeMultipliers[0xf1];
        Assert.That(meter.TxZkGasUsed, Is.EqualTo(expected));
    }

    /// <summary>
    /// Deferred step is flushed with spawn estimate by
    /// <see cref="ZkGasTxTracer.ReportActionEnd(long, ReadOnlyMemory{byte})"/>
    /// (call-type action end), even without a subsequent opcode.
    /// </summary>
    [Test]
    public void SpawnOpcode_FlushedByReportActionEnd_Call()
    {
        (ZkGasTxTracer tracer, ZkGasMeter meter) = Make();

        tracer.StartOperation(0, Instruction.CALL, gas: 40_000, env: Env());
        tracer.ReportOperationRemainingGas(20_000);
        tracer.ReportAction(20_000, UInt256.Zero, TestItem.AddressA, TestItem.AddressB,
            ReadOnlyMemory<byte>.Empty, ExecutionType.CALL);
        tracer.ReportActionEnd(10_000, ReadOnlyMemory<byte>.Empty);

        Assert.That(meter.TxZkGasUsed, Is.EqualTo(ZkGasSchedule.SpawnEstimateCall * ZkGasSchedule.OpcodeMultipliers[0xf1]));
    }

    /// <summary>
    /// Deferred step is flushed with spawn estimate by
    /// <see cref="ZkGasTxTracer.ReportActionEnd(long, Address, ReadOnlyMemory{byte})"/>
    /// (create-type action end).
    /// </summary>
    [Test]
    public void SpawnOpcode_FlushedByReportActionEnd_Create()
    {
        (ZkGasTxTracer tracer, ZkGasMeter meter) = Make();

        tracer.StartOperation(0, Instruction.CREATE, gas: 60_000, env: Env());
        tracer.ReportOperationRemainingGas(40_000);
        tracer.ReportAction(40_000, UInt256.Zero, TestItem.AddressA, TestItem.AddressA,
            ReadOnlyMemory<byte>.Empty, ExecutionType.CREATE);
        tracer.ReportActionEnd(30_000, TestItem.AddressC, ReadOnlyMemory<byte>.Empty);

        Assert.That(meter.TxZkGasUsed, Is.EqualTo(ZkGasSchedule.SpawnEstimateCreate * ZkGasSchedule.OpcodeMultipliers[0xf0]));
    }

    /// <summary>
    /// Deferred step is flushed with spawn estimate by
    /// <see cref="ZkGasTxTracer.ReportActionError"/>.
    /// </summary>
    [Test]
    public void SpawnOpcode_FlushedByReportActionError()
    {
        (ZkGasTxTracer tracer, ZkGasMeter meter) = Make();

        tracer.StartOperation(0, Instruction.CALL, gas: 30_000, env: Env());
        tracer.ReportOperationRemainingGas(15_000);
        tracer.ReportAction(15_000, UInt256.Zero, TestItem.AddressA, TestItem.AddressB,
            ReadOnlyMemory<byte>.Empty, ExecutionType.CALL);
        tracer.ReportActionError(EvmExceptionType.OutOfGas);

        Assert.That(meter.TxZkGasUsed, Is.EqualTo(ZkGasSchedule.SpawnEstimateCall * ZkGasSchedule.OpcodeMultipliers[0xf1]));
    }

    /// <summary>
    /// Deferred step is flushed by <see cref="ZkGasTxTracer.ReportActionRevert"/>.
    /// </summary>
    [Test]
    public void SpawnOpcode_FlushedByReportActionRevert()
    {
        (ZkGasTxTracer tracer, ZkGasMeter meter) = Make();

        tracer.StartOperation(0, Instruction.CALL, gas: 30_000, env: Env());
        tracer.ReportOperationRemainingGas(15_000);
        tracer.ReportAction(15_000, UInt256.Zero, TestItem.AddressA, TestItem.AddressB,
            ReadOnlyMemory<byte>.Empty, ExecutionType.CALL);
        tracer.ReportActionRevert(0, ReadOnlyMemory<byte>.Empty);

        Assert.That(meter.TxZkGasUsed, Is.EqualTo(ZkGasSchedule.SpawnEstimateCall * ZkGasSchedule.OpcodeMultipliers[0xf1]));
    }

    // ── precompile charging ───────────────────────────────────────────────────

    /// <summary>
    /// A precompile call that succeeds is charged based on
    /// (<c>gasStart − gasEnd</c>) × multiplier.
    /// </summary>
    [Test]
    public void Precompile_ChargedOnReportActionEnd_WithGasUsed()
    {
        (ZkGasTxTracer tracer, ZkGasMeter meter) = Make();

        Address ecrecover = new("0x0000000000000000000000000000000000000001");
        // ReportAction with isPrecompileCall = true
        tracer.ReportAction(1000, UInt256.Zero, TestItem.AddressA, ecrecover,
            ReadOnlyMemory<byte>.Empty, ExecutionType.CALL, isPrecompileCall: true);
        tracer.ReportActionEnd(400, ReadOnlyMemory<byte>.Empty); // 600 gas used

        ulong expected = 600UL * ZkGasSchedule.PrecompileMultipliers[0x01]; // ecrecover = address byte 1
        Assert.That(meter.TxZkGasUsed, Is.EqualTo(expected));
    }

    /// <summary>
    /// <see cref="ZkGasTxTracer.ReportActionError"/> passes <c>gasRemaining = 0</c> to
    /// <c>ChargePrecompileIfPending</c>. This charges the full forwarded gas, mirroring
    /// the EVM specification where a precompile error burns all forwarded gas.
    /// </summary>
    [Test]
    public void Precompile_ChargedFullGas_OnReportActionError()
    {
        (ZkGasTxTracer tracer, ZkGasMeter meter) = Make();

        Address ecrecover = new("0x0000000000000000000000000000000000000001");
        tracer.ReportAction(800, UInt256.Zero, TestItem.AddressA, ecrecover,
            ReadOnlyMemory<byte>.Empty, ExecutionType.CALL, isPrecompileCall: true);
        tracer.ReportActionError(EvmExceptionType.PrecompileFailure);

        // gasStart=800, gasRemaining=0 → gasUsed=800
        ulong expected = 800UL * ZkGasSchedule.PrecompileMultipliers[0x01];
        Assert.That(meter.TxZkGasUsed, Is.EqualTo(expected));
    }

    /// <summary>
    /// A precompile that reverts charges gas based on actual gas used (not full forwarded).
    /// </summary>
    [Test]
    public void Precompile_ChargedActualGas_OnReportActionRevert()
    {
        (ZkGasTxTracer tracer, ZkGasMeter meter) = Make();

        Address sha256 = new("0x0000000000000000000000000000000000000002");
        tracer.ReportAction(1000, UInt256.Zero, TestItem.AddressA, sha256,
            ReadOnlyMemory<byte>.Empty, ExecutionType.CALL, isPrecompileCall: true);
        tracer.ReportActionRevert(300, ReadOnlyMemory<byte>.Empty); // 700 gas used

        ulong expected = 700UL * ZkGasSchedule.PrecompileMultipliers[0x02]; // sha256 = byte 2
        Assert.That(meter.TxZkGasUsed, Is.EqualTo(expected));
    }

    // ── spawn + precompile interaction ────────────────────────────────────────

    /// <summary>
    /// When a CALL to a precompile occurs, the precompile flag takes precedence.
    /// The spawn deferred step is flushed at ReportActionEnd with the spawn estimate,
    /// and the precompile charge is also applied.
    /// </summary>
    [Test]
    public void SpawnOpcode_AndPrecompile_BothCharged()
    {
        (ZkGasTxTracer tracer, ZkGasMeter meter) = Make();

        Address identity = new("0x0000000000000000000000000000000000000004");
        tracer.StartOperation(0, Instruction.CALL, gas: 10_000, env: Env());
        tracer.ReportOperationRemainingGas(9_000); // 1_000 raw — should be replaced by spawn estimate
        tracer.ReportAction(9_000, UInt256.Zero, TestItem.AddressA, identity,
            ReadOnlyMemory<byte>.Empty, ExecutionType.CALL, isPrecompileCall: true);
        tracer.ReportActionEnd(8_500, ReadOnlyMemory<byte>.Empty); // precompile used 500 gas

        ulong spawnCharge = ZkGasSchedule.SpawnEstimateCall * ZkGasSchedule.OpcodeMultipliers[0xf1];
        ulong precompileCharge = 500UL * ZkGasSchedule.PrecompileMultipliers[0x04]; // identity = byte 4
        Assert.That(meter.TxZkGasUsed, Is.EqualTo(spawnCharge + precompileCharge));
    }

    // ── IsTracingActions / IsTracingInstructions flags ────────────────────────

    /// <summary>
    /// Both <see cref="ZkGasTxTracer.IsTracingActions"/> and
    /// <see cref="ZkGasTxTracer.IsTracingInstructions"/> must be <c>true</c> so that
    /// the VM delivers the callbacks this tracer relies on.
    /// </summary>
    [Test]
    public void TracerFlags_AreEnabled()
    {
        (ZkGasTxTracer tracer, ZkGasMeter _) = Make();
        Assert.That(tracer.IsTracingActions, Is.True);
        Assert.That(tracer.IsTracingInstructions, Is.True);
    }
}
