// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Int256;
using NUnit.Framework;
using Nethermind.Specs;

namespace Nethermind.Evm.Test;

[TestFixture]
[Parallelizable(ParallelScope.Self)]
public class VirtualMachineTests : VirtualMachineTestsBase
{
    [Test]
    public void Stop()
    {
        TestAllTracerWithOutput receipt = Execute((byte)Instruction.STOP);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction));
    }

    [Test]
    public void Trace()
    {
        GethLikeTxTrace trace = ExecuteAndTrace(
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.ADD,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);

        Assert.That(trace.Entries.Count, Is.EqualTo(5), "number of entries");
        GethTxTraceEntry entry = trace.Entries[1];
        Assert.That(entry.Depth, Is.EqualTo(1), nameof(entry.Depth));
        Assert.That(entry.Gas, Is.EqualTo(79000 - GasCostOf.VeryLow), nameof(entry.Gas));
        Assert.That(entry.GasCost, Is.EqualTo(GasCostOf.VeryLow), nameof(entry.GasCost));
        Assert.That(entry.Memory.Count, Is.EqualTo(0), nameof(entry.Memory));
        Assert.That(entry.Stack.Count, Is.EqualTo(1), nameof(entry.Stack));
        Assert.That(trace.Entries[4].Storage.Count, Is.EqualTo(1), nameof(entry.Storage));
        Assert.That(entry.ProgramCounter, Is.EqualTo(2), nameof(entry.ProgramCounter));
        Assert.That(entry.Opcode, Is.EqualTo("PUSH1"), nameof(entry.Opcode));
    }

    [Test]
    public void Trace_vm_errors()
    {
        GethLikeTxTrace trace = ExecuteAndTrace(1L, 21000L + 19000L,
            (byte)Instruction.PUSH1,
            1,
            (byte)Instruction.PUSH1,
            1,
            (byte)Instruction.ADD,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);

        Assert.True(trace.Entries.Any(e => e.Error is not null));
    }

    [Test]
    public void Trace_memory_out_of_gas_exception()
    {
        byte[] code = Prepare.EvmCode
            .PushData((UInt256)(10 * 1000 * 1000))
            .Op(Instruction.MLOAD)
            .Done;

        GethLikeTxTrace trace = ExecuteAndTrace(1L, 21000L + 19000L, code);

        Assert.True(trace.Entries.Any(e => e.Error is not null));
    }

    [Test]
    [Ignore("// https://github.com/NethermindEth/nethermind/issues/140")]
    public void Trace_invalid_jump_exception()
    {
        byte[] code = Prepare.EvmCode
            .PushData(255)
            .Op(Instruction.JUMP)
            .Done;

        GethLikeTxTrace trace = ExecuteAndTrace(1L, 21000L + 19000L, code);

        Assert.True(trace.Entries.Any(e => e.Error is not null));
    }

    [Test]
    [Ignore("// https://github.com/NethermindEth/nethermind/issues/140")]
    public void Trace_invalid_jumpi_exception()
    {
        byte[] code = Prepare.EvmCode
            .PushData(1)
            .PushData(255)
            .Op(Instruction.JUMPI)
            .Done;

        GethLikeTxTrace trace = ExecuteAndTrace(1L, 21000L + 19000L, code);

        Assert.True(trace.Entries.Any(e => e.Error is not null));
    }

    [Test(Description = "Test a case where the trace is created for one transaction and subsequent untraced transactions keep adding entries to the first trace created.")]
    public void Trace_each_tx_separate()
    {
        GethLikeTxTrace trace = ExecuteAndTrace(
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.ADD,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);

        Execute(
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.ADD,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);

        Assert.That(trace.Entries.Count, Is.EqualTo(5), "number of entries");
        GethTxTraceEntry entry = trace.Entries[1];
        Assert.That(entry.Depth, Is.EqualTo(1), nameof(entry.Depth));
        Assert.That(entry.Gas, Is.EqualTo(79000 - GasCostOf.VeryLow), nameof(entry.Gas));
        Assert.That(entry.GasCost, Is.EqualTo(GasCostOf.VeryLow), nameof(entry.GasCost));
        Assert.That(entry.Memory.Count, Is.EqualTo(0), nameof(entry.Memory));
        Assert.That(entry.Stack.Count, Is.EqualTo(1), nameof(entry.Stack));
        Assert.That(trace.Entries[4].Storage.Count, Is.EqualTo(1), nameof(entry.Storage));
        Assert.That(entry.ProgramCounter, Is.EqualTo(2), nameof(entry.ProgramCounter));
        Assert.That(entry.Opcode, Is.EqualTo("PUSH1"), nameof(entry.Opcode));
    }

    [Test]
    public void Add_0_0()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.ADD,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SReset), "gas");
        Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(new byte[] { 0 }), "storage");
    }

    [Test]
    public void Add_0_1()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.PUSH1,
            1,
            (byte)Instruction.ADD,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SSet), "gas");
        Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(new byte[] { 1 }), "storage");
    }

    [Test]
    public void Add_1_0()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            1,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.ADD,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SSet), "gas");
        Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(new byte[] { 1 }), "storage");
    }

    [Test]
    public void Mstore()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            96, // data
            (byte)Instruction.PUSH1,
            64, // position
            (byte)Instruction.MSTORE);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.Memory * 3), "gas");
    }

    [Test]
    public void Mstore_twice_same_location()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            96,
            (byte)Instruction.PUSH1,
            64,
            (byte)Instruction.MSTORE,
            (byte)Instruction.PUSH1,
            96,
            (byte)Instruction.PUSH1,
            64,
            (byte)Instruction.MSTORE);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 6 + GasCostOf.Memory * 3), "gas");
    }

    [Test]
    public void Mload()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            64, // position
            (byte)Instruction.MLOAD);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 2 + GasCostOf.Memory * 3), "gas");
    }

    [Test]
    public void Mload_after_mstore()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            96,
            (byte)Instruction.PUSH1,
            64,
            (byte)Instruction.MSTORE,
            (byte)Instruction.PUSH1,
            64,
            (byte)Instruction.MLOAD);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 5 + GasCostOf.Memory * 3), "gas");
    }

    [Test]
    public void Dup1()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.DUP1);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 2), "gas");
    }

    [Test]
    public void Codecopy()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            32, // length
            (byte)Instruction.PUSH1,
            0, // src
            (byte)Instruction.PUSH1,
            32, // dest
            (byte)Instruction.CODECOPY);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 4 + GasCostOf.Memory * 3), "gas");
    }

    [Test]
    public void Swap()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            32, // length
            (byte)Instruction.PUSH1,
            0, // src
            (byte)Instruction.SWAP1);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3), "gas");
    }

    [Test]
    public void Sload()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            0, // index
            (byte)Instruction.SLOAD);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 1 + GasCostOf.SLoadEip150), "gas");
    }

    [Test]
    public void Exp_2_160()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            160,
            (byte)Instruction.PUSH1,
            2,
            (byte)Instruction.EXP,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.SSet + GasCostOf.Exp + GasCostOf.ExpByteEip160), "gas");
        Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(BigInteger.Pow(2, 160).ToBigEndianByteArray()), "storage");
    }

    [Test]
    public void Exp_0_0()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.EXP,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.Exp + GasCostOf.SSet), "gas");
        Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(BigInteger.One.ToBigEndianByteArray()), "storage");
    }

    [Test]
    public void Exp_0_160()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            160,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.EXP,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.Exp + GasCostOf.ExpByteEip160 + GasCostOf.SReset), "gas");
        Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(BigInteger.Zero.ToBigEndianByteArray()), "storage");
    }

    [Test]
    public void Exp_1_160()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            160,
            (byte)Instruction.PUSH1,
            1,
            (byte)Instruction.EXP,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.Exp + GasCostOf.ExpByteEip160 + GasCostOf.SSet), "gas");
        Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(BigInteger.One.ToBigEndianByteArray()), "storage");
    }

    [Test]
    public void Sub_0_0()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SUB,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 4 + GasCostOf.SReset), "gas");
        Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(new byte[] { 0 }), "storage");
    }

    [Test]
    public void Not_0()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.NOT,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.SSet), "gas");
        Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo((BigInteger.Pow(2, 256) - 1).ToBigEndianByteArray()), "storage");
    }

    [Test]
    public void Or_0_0()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.OR,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 4 + GasCostOf.SReset), "gas");
        Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(BigInteger.Zero.ToBigEndianByteArray()), "storage");
    }

    [Test]
    public void Sstore_twice_0_same_storage_should_refund_only_once()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 2 + GasCostOf.SReset), "gas");
        Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(BigInteger.Zero.ToBigEndianByteArray()), "storage");
    }

    /// <summary>
    /// TLoad gas cost check
    /// </summary>
    [Test]
    public void Tload()
    {
        byte[] code = Prepare.EvmCode
            .PushData(96)
            .Op(Instruction.TLOAD)
            .Done;

        TestAllTracerWithOutput receipt = Execute((MainnetSpecProvider.ParisBlockNumber, MainnetSpecProvider.CancunBlockTimestamp), 100000, code);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 1 + GasCostOf.TLoad), "gas");
    }

    /// <summary>
    /// MCOPY gas cost check
    /// </summary>
    [Test]
    public void MCopy()
    {
        byte[] data = new byte[] { 0x60, 0x17, 0x60, 0x03, 0x02, 0x00 };
        byte[] code = Prepare.EvmCode
            .MSTORE(0, data.PadRight(32))
            .MCOPY(6, 0, 6)
            .STOP()
            .Done;
        GethLikeTxTrace traces = Execute(new GethLikeTxMemoryTracer(GethTraceOptions.Default), code, MainnetSpecProvider.CancunActivation).BuildResult();

        Assert.That(traces.Entries[^2].GasCost, Is.EqualTo(GasCostOf.VeryLow + GasCostOf.VeryLow * ((data.Length + 31) / 32) + GasCostOf.Memory * 0), "gas");
    }

    [Test]
    public void MCopy_exclusive_areas()
    {
        byte[] data = Bytes.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        byte[] bytecode = Prepare.EvmCode
            .MSTORE(0, data)
            .MCOPY(32, 0, 32)
            .STOP()
            .Done;
        GethLikeTxTrace traces = Execute(
            new GethLikeTxMemoryTracer(GethTraceOptions.Default with { EnableMemory = true }),
            bytecode,
            MainnetSpecProvider.CancunActivation)
            .BuildResult();

        var copied = traces.Entries.Last().Memory[0];
        var origin = traces.Entries.Last().Memory[1];

        Assert.That(traces.Entries[^2].GasCost, Is.EqualTo(GasCostOf.VeryLow + GasCostOf.VeryLow * ((data.Length + 31) / 32) + GasCostOf.Memory * 1), "gas");
        Assert.That(origin, Is.EqualTo(copied));
    }


    [Test]
    public void MCopy_Overwrite_areas_copy_right()
    {
        int SLICE_SIZE = 8;
        byte[] data = Bytes.FromHexString("0102030405060708000000000000000000000000000000000000000000000000");
        byte[] bytecode = Prepare.EvmCode
            .MSTORE(0, data)
            .MCOPY(1, 0, (UInt256)SLICE_SIZE)
            .STOP()
            .Done;
        GethLikeTxTrace traces = Execute(
            new GethLikeTxMemoryTracer(GethTraceOptions.Default with { EnableMemory = true }),
            bytecode,
            MainnetSpecProvider.CancunActivation)
            .BuildResult();

        var result = traces.Entries.Last().Memory[0];

        Assert.That(traces.Entries[^2].GasCost, Is.EqualTo(GasCostOf.VeryLow + GasCostOf.VeryLow * (SLICE_SIZE + 31) / 32), "gas");
        Assert.That(result, Is.EqualTo("0101020304050607080000000000000000000000000000000000000000000000"), "memory state");
    }

    [Test]
    public void MCopy_twice_same_location()
    {
        byte[] data = Bytes.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        byte[] bytecode = Prepare.EvmCode
            .MSTORE(0, data)
            .MCOPY(0, 0, 32)
            .STOP()
            .Done;
        GethLikeTxTrace traces = Execute(
            new GethLikeTxMemoryTracer(GethTraceOptions.Default with { EnableMemory = true }),
            bytecode,
            MainnetSpecProvider.CancunActivation)
            .BuildResult();

        Assert.That(traces.Entries[^2].GasCost, Is.EqualTo(GasCostOf.VeryLow + GasCostOf.VeryLow * ((data.Length + 31) / 32)), "gas");
        Assert.That(traces.Entries.Last().Memory.Count, Is.EqualTo(1));
    }

    [Test]
    public void MCopy_Overwrite_areas_copy_left()
    {
        int SLICE_SIZE = 8;
        byte[] data = Bytes.FromHexString("0001020304050607080000000000000000000000000000000000000000000000");
        byte[] bytecode = Prepare.EvmCode
            .MSTORE(0, data)
            .MCOPY(0, 1, (UInt256)SLICE_SIZE)
            .STOP()
            .Done;
        GethLikeTxTrace traces = Execute(
            new GethLikeTxMemoryTracer(GethTraceOptions.Default with { EnableMemory = true }),
            bytecode,
            MainnetSpecProvider.CancunActivation)
            .BuildResult();

        var result = traces.Entries.Last().Memory[0];

        Assert.That(traces.Entries[^2].GasCost, Is.EqualTo(GasCostOf.VeryLow + GasCostOf.VeryLow * (SLICE_SIZE + 31) / 32), "gas");
        Assert.That(result, Is.EqualTo("0102030405060708080000000000000000000000000000000000000000000000"), "memory state");
    }

    /// <summary>
    /// TStore gas cost check
    /// </summary>
    [Test]
    public void Tstore()
    {
        byte[] code = Prepare.EvmCode
            .PushData(96)
            .PushData(64)
            .Op(Instruction.TSTORE)
            .Done;

        TestAllTracerWithOutput receipt = Execute((MainnetSpecProvider.ParisBlockNumber, MainnetSpecProvider.CancunBlockTimestamp), 100000, code);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 2 + GasCostOf.TStore), "gas");
    }

    [Test]
    public void Revert()
    {
        // See: https://eips.ethereum.org/EIPS/eip-140

        byte[] code = Bytes.FromHexString("0x6c726576657274656420646174616000557f726576657274206d657373616765000000000000000000000000000000000000600052600e6000fd");
        TestAllTracerWithOutput receipt = Execute(blockNumber: MainnetSpecProvider.ByzantiumBlockNumber, 100_000, code);

        Assert.That(receipt.Error, Is.EqualTo("Reverted 0x726576657274206d657373616765"));
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + 20024));
    }
}
