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

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class ZkGasTxTracerTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static (ZkGasTxTracer tracer, ZkGasMeter meter) Make()
    {
        ZkGasMeter meter = new(
            opcodeMultipliers: ZkGasTestSchedules.OpcodeMultipliers,
            precompileMultipliers: ZkGasTestSchedules.PrecompileMultipliers);
        ZkGasTxTracer tracer = new(meter);
        return (tracer!, meter);
    }

    private static ExecutionEnvironment Env() => null!;

    // ── non-spawn opcode ──────────────────────────────────────────────────────

    [Test]
    public void NonSpawnOpcode_ChargedImmediately()
    {
        (ZkGasTxTracer tracer, ZkGasMeter meter) = Make();

        tracer.StartOperation(0, Instruction.ADD, gas: 100, env: Env());
        tracer.ReportOperationRemainingGas(97);

        ulong expected = 3UL * ZkGasTestSchedules.OpcodeMultipliers.Span[0x01];
        Assert.That(meter.TxZkGasUsed, Is.EqualTo(expected));
    }

    [Test]
    public void NonSpawnOpcode_Accumulates()
    {
        (ZkGasTxTracer tracer, ZkGasMeter meter) = Make();

        tracer.StartOperation(0, Instruction.ADD, gas: 100, env: Env());
        tracer.ReportOperationRemainingGas(97);

        tracer.StartOperation(1, Instruction.MUL, gas: 97, env: Env());
        tracer.ReportOperationRemainingGas(94);

        ulong expected = 3UL * ZkGasTestSchedules.OpcodeMultipliers.Span[0x02]
                       + 3UL * ZkGasTestSchedules.OpcodeMultipliers.Span[0x01];
        Assert.That(meter.TxZkGasUsed, Is.EqualTo(expected));
    }

    // ── spawn opcode — deferred, spawned ─────────────────────────────────────

    [Test]
    public void SpawnOpcode_Deferred_UsesSpawnEstimate_WhenChildDispatched()
    {
        (ZkGasTxTracer tracer, ZkGasMeter meter) = Make();

        tracer.StartOperation(0, Instruction.CALL, gas: 50_000, env: Env());
        tracer.ReportOperationRemainingGas(25_000);
        tracer.ReportAction(25_000, UInt256.Zero, TestItem.AddressA, TestItem.AddressB,
            ReadOnlyMemory<byte>.Empty, ExecutionType.CALL);
        tracer.StartOperation(1, Instruction.STOP, gas: 0, env: Env());
        tracer.ReportOperationRemainingGas(0);

        ulong spawnCharge = ZkGasSchedule.SpawnEstimateCall * ZkGasTestSchedules.OpcodeMultipliers.Span[0xf1];
        ulong stopCharge = 0;
        Assert.That(meter.TxZkGasUsed, Is.EqualTo(spawnCharge + stopCharge));
    }

    [Test]
    public void SpawnOpcode_Deferred_UsesRawGas_WhenNoChildDispatched()
    {
        (ZkGasTxTracer tracer, ZkGasMeter meter) = Make();

        tracer.StartOperation(0, Instruction.CALL, gas: 50_000, env: Env());
        tracer.ReportOperationRemainingGas(47_000);
        tracer.StartOperation(1, Instruction.STOP, gas: 0, env: Env());
        tracer.ReportOperationRemainingGas(0);

        ulong expected = 3_000UL * ZkGasTestSchedules.OpcodeMultipliers.Span[0xf1];
        Assert.That(meter.TxZkGasUsed, Is.EqualTo(expected));
    }

    // ── spawn opcode — CREATE post-trace bail vs error (regression) ──────────

    [Test]
    public void SpawnOpcode_Create_PostTraceBail_UsesSpawnEstimate()
    {
        (ZkGasTxTracer tracer, ZkGasMeter meter) = Make();

        tracer.StartOperation(0, Instruction.CREATE, gas: 50_000, env: Env());
        tracer.ReportOperationRemainingGas(49_968);
        tracer.StartOperation(1, Instruction.STOP, gas: 0, env: Env());
        tracer.ReportOperationRemainingGas(0);

        ulong spawnCharge = ZkGasSchedule.SpawnEstimateCreate * ZkGasTestSchedules.OpcodeMultipliers.Span[0xf0];
        Assert.That(meter.TxZkGasUsed, Is.EqualTo(spawnCharge));
    }

    [Test]
    public void SpawnOpcode_Create_OutOfGasAfterTrace_UsesRawDelta()
    {
        (ZkGasTxTracer tracer, ZkGasMeter meter) = Make();

        tracer.StartOperation(0, Instruction.CREATE, gas: 50_000, env: Env());
        tracer.ReportOperationRemainingGas(49_968);
        tracer.ReportOperationError(EvmExceptionType.OutOfGas);
        tracer.StartOperation(1, Instruction.STOP, gas: 0, env: Env());
        tracer.ReportOperationRemainingGas(0);

        ulong rawCharge = 32UL * ZkGasTestSchedules.OpcodeMultipliers.Span[0xf0];
        Assert.That(meter.TxZkGasUsed, Is.EqualTo(rawCharge));
    }

    [Test]
    public void SpawnOpcode_FlushedByReportActionEnd_Call()
    {
        (ZkGasTxTracer tracer, ZkGasMeter meter) = Make();

        tracer.StartOperation(0, Instruction.CALL, gas: 40_000, env: Env());
        tracer.ReportOperationRemainingGas(20_000);
        tracer.ReportAction(20_000, UInt256.Zero, TestItem.AddressA, TestItem.AddressB,
            ReadOnlyMemory<byte>.Empty, ExecutionType.CALL);
        tracer.ReportActionEnd(10_000, ReadOnlyMemory<byte>.Empty);

        Assert.That(meter.TxZkGasUsed, Is.EqualTo(ZkGasSchedule.SpawnEstimateCall * ZkGasTestSchedules.OpcodeMultipliers.Span[0xf1]));
    }

    [Test]
    public void SpawnOpcode_FlushedByReportActionEnd_Create()
    {
        (ZkGasTxTracer tracer, ZkGasMeter meter) = Make();

        tracer.StartOperation(0, Instruction.CREATE, gas: 60_000, env: Env());
        tracer.ReportOperationRemainingGas(40_000);
        tracer.ReportAction(40_000, UInt256.Zero, TestItem.AddressA, TestItem.AddressA,
            ReadOnlyMemory<byte>.Empty, ExecutionType.CREATE);
        tracer.ReportActionEnd(30_000, TestItem.AddressC, ReadOnlyMemory<byte>.Empty);

        Assert.That(meter.TxZkGasUsed, Is.EqualTo(ZkGasSchedule.SpawnEstimateCreate * ZkGasTestSchedules.OpcodeMultipliers.Span[0xf0]));
    }

    [Test]
    public void SpawnOpcode_FlushedByReportActionError()
    {
        (ZkGasTxTracer tracer, ZkGasMeter meter) = Make();

        tracer.StartOperation(0, Instruction.CALL, gas: 30_000, env: Env());
        tracer.ReportOperationRemainingGas(15_000);
        tracer.ReportAction(15_000, UInt256.Zero, TestItem.AddressA, TestItem.AddressB,
            ReadOnlyMemory<byte>.Empty, ExecutionType.CALL);
        tracer.ReportActionError(EvmExceptionType.OutOfGas);

        Assert.That(meter.TxZkGasUsed, Is.EqualTo(ZkGasSchedule.SpawnEstimateCall * ZkGasTestSchedules.OpcodeMultipliers.Span[0xf1]));
    }

    [Test]
    public void SpawnOpcode_FlushedByReportActionRevert()
    {
        (ZkGasTxTracer tracer, ZkGasMeter meter) = Make();

        tracer.StartOperation(0, Instruction.CALL, gas: 30_000, env: Env());
        tracer.ReportOperationRemainingGas(15_000);
        tracer.ReportAction(15_000, UInt256.Zero, TestItem.AddressA, TestItem.AddressB,
            ReadOnlyMemory<byte>.Empty, ExecutionType.CALL);
        tracer.ReportActionRevert(0, ReadOnlyMemory<byte>.Empty);

        Assert.That(meter.TxZkGasUsed, Is.EqualTo(ZkGasSchedule.SpawnEstimateCall * ZkGasTestSchedules.OpcodeMultipliers.Span[0xf1]));
    }

    // ── precompile charging ───────────────────────────────────────────────────

    [Test]
    public void Precompile_ChargedOnReportActionEnd_WithGasUsed()
    {
        (ZkGasTxTracer tracer, ZkGasMeter meter) = Make();

        Address ecrecover = new("0x0000000000000000000000000000000000000001");
        tracer.ReportAction(1000, UInt256.Zero, TestItem.AddressA, ecrecover,
            ReadOnlyMemory<byte>.Empty, ExecutionType.CALL, isPrecompileCall: true);
        tracer.ReportActionEnd(400, ReadOnlyMemory<byte>.Empty); // 600 gas used

        ulong expected = 600UL * ZkGasTestSchedules.PrecompileMultiplier(0x01);
        Assert.That(meter.TxZkGasUsed, Is.EqualTo(expected));
    }

    [Test]
    public void Precompile_ChargedFullGas_OnReportActionError()
    {
        (ZkGasTxTracer tracer, ZkGasMeter meter) = Make();

        Address ecrecover = new("0x0000000000000000000000000000000000000001");
        tracer.ReportAction(800, UInt256.Zero, TestItem.AddressA, ecrecover,
            ReadOnlyMemory<byte>.Empty, ExecutionType.CALL, isPrecompileCall: true);
        tracer.ReportActionError(EvmExceptionType.PrecompileFailure);

        ulong expected = 800UL * ZkGasTestSchedules.PrecompileMultiplier(0x01);
        Assert.That(meter.TxZkGasUsed, Is.EqualTo(expected));
    }

    [Test]
    public void Precompile_ChargedActualGas_OnReportActionRevert()
    {
        (ZkGasTxTracer tracer, ZkGasMeter meter) = Make();

        Address sha256 = new("0x0000000000000000000000000000000000000002");
        tracer.ReportAction(1000, UInt256.Zero, TestItem.AddressA, sha256,
            ReadOnlyMemory<byte>.Empty, ExecutionType.CALL, isPrecompileCall: true);
        tracer.ReportActionRevert(300, ReadOnlyMemory<byte>.Empty);

        ulong expected = 700UL * ZkGasTestSchedules.PrecompileMultiplier(0x02);
        Assert.That(meter.TxZkGasUsed, Is.EqualTo(expected));
    }

    // ── spawn + precompile interaction ────────────────────────────────────────

    [Test]
    public void SpawnOpcode_AndPrecompile_BothCharged()
    {
        (ZkGasTxTracer tracer, ZkGasMeter meter) = Make();

        Address identity = new("0x0000000000000000000000000000000000000004");
        tracer.StartOperation(0, Instruction.CALL, gas: 10_000, env: Env());
        tracer.ReportOperationRemainingGas(9_000);
        tracer.ReportAction(9_000, UInt256.Zero, TestItem.AddressA, identity,
            ReadOnlyMemory<byte>.Empty, ExecutionType.CALL, isPrecompileCall: true);
        tracer.ReportActionEnd(8_500, ReadOnlyMemory<byte>.Empty); // 500 gas used

        ulong spawnCharge = ZkGasSchedule.SpawnEstimateCall * ZkGasTestSchedules.OpcodeMultipliers.Span[0xf1];
        ulong precompileCharge = 500UL * ZkGasTestSchedules.PrecompileMultiplier(0x04);
        Assert.That(meter.TxZkGasUsed, Is.EqualTo(spawnCharge + precompileCharge));
    }

    // ── IsTracingActions / IsTracingInstructions flags ────────────────────────

    [Test]
    public void TracerFlags_AreEnabled()
    {
        (ZkGasTxTracer tracer, ZkGasMeter _) = Make();
        Assert.That(tracer.IsTracingActions, Is.True);
        Assert.That(tracer.IsTracingInstructions, Is.True);
    }
}
