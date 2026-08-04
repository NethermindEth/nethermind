// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Numerics;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Crypto;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Test.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using NUnit.Framework;
using Nethermind.Specs;

namespace Nethermind.Evm.Test;

[Parallelizable(ParallelScope.Self)]
public class VirtualMachineTests : VirtualMachineTestsBase
{
    private static readonly TestCaseData[] JumpCompletionCases =
    [
        new TestCaseData("600456005b00", 21012UL, 4).SetName("Jump_taken"),
        new TestCaseData("6001600657005b00", 21017UL, 5).SetName("JumpI_taken"),
        new TestCaseData("6000600657005b00", 21016UL, 4).SetName("JumpI_not_taken"),
        new TestCaseData("6003565b00", 21012UL, 4).SetName("Jump_to_next_instruction"),
        new TestCaseData("600456fe5b5b00", 21013UL, 5).SetName("Jump_to_consecutive_markers"),
        new TestCaseData("6003565b", 21012UL, 3).SetName("Jump_to_final_byte"),
    ];

    private static readonly TestCaseData[] JumpFailureCases =
    [
        new TestCaseData("56", 100000UL, 1).SetName("Jump_stack_underflow"),
        new TestCaseData("600056", 100000UL, 2).SetName("Jump_invalid_destination"),
        new TestCaseData("6003565b", 21010UL, 2).SetName("Jump_charge_out_of_gas"),
        new TestCaseData("6003565b", 21011UL, 3).SetName("JumpDest_charge_out_of_gas_after_Jump"),
        new TestCaseData("60016005575b", 21015UL, 3).SetName("JumpI_charge_out_of_gas"),
        new TestCaseData("60016005575b", 21016UL, 4).SetName("JumpDest_charge_out_of_gas_after_JumpI"),
    ];

    private sealed class NoInstructionTracer : TestAllTracerWithOutput
    {
        public override bool IsTracingInstructions => false;
    }

    [Test]
    public void Stop()
    {
        TestAllTracerWithOutput receipt = Execute((byte)Instruction.STOP);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction));
    }

    [TestCaseSource(nameof(JumpCompletionCases))]
    public void Untraced_jump_completion_preserves_semantics(string bytecode, ulong expectedGas, int expectedOpCodeCount)
    {
        TestAllTracerWithOutput receipt = ExecuteUntraced(100000UL, Bytes.FromHexString(bytecode));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success), "status");
            Assert.That(receipt.GasSpent, Is.EqualTo(expectedGas), "gas");
            Assert.That(Machine.OpCodeCount, Is.EqualTo(expectedOpCodeCount), "opcode count");
        }
    }

    [TestCaseSource(nameof(JumpFailureCases))]
    public void Untraced_jump_completion_preserves_failure_ordering(string bytecode, ulong gasLimit, int expectedOpCodeCount)
    {
        TestAllTracerWithOutput receipt = ExecuteUntraced(gasLimit, Bytes.FromHexString(bytecode));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Failure), "status");
            Assert.That(receipt.GasSpent, Is.EqualTo(gasLimit), "gas");
            Assert.That(Machine.OpCodeCount, Is.EqualTo(expectedOpCodeCount), "opcode count");
        }
    }

    [TestCase(Instruction.JUMP, "600456005b00", 4)]
    [TestCase(Instruction.JUMPI, "6001600657005b00", 6)]
    public void Traced_taken_jump_keeps_jumpdest_visible(Instruction instruction, string bytecode, int target)
    {
        GethLikeTxTrace trace = ExecuteAndTrace(Bytes.FromHexString(bytecode));
        GethTxTraceEntry jumpDest = trace.Entries.Single(static entry => entry.Opcode == nameof(Instruction.JUMPDEST));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(jumpDest.ProgramCounter, Is.EqualTo(target), $"{instruction} target");
            Assert.That(jumpDest.GasCost, Is.EqualTo(GasCostOf.JumpDest), $"{instruction} gas");
        }
    }

    private TestAllTracerWithOutput ExecuteUntraced(ulong gasLimit, byte[] code)
    {
        (Block block, Transaction transaction) = PrepareTx(Activation, gasLimit, code);
        NoInstructionTracer tracer = new();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        return tracer;
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

        AssertFirstPushTrace(trace);
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

        Assert.That(trace.Entries.Any(static e => e.Error is not null), Is.True);
    }

    [Test]
    public void Trace_memory_out_of_gas_exception()
    {
        byte[] code = Prepare.EvmCode
            .PushData((UInt256)(10 * 1000 * 1000))
            .Op(Instruction.MLOAD)
            .Done;

        GethLikeTxTrace trace = ExecuteAndTrace(1L, 21000L + 19000L, code);

        Assert.That(trace.Entries.Any(static e => e.Error is not null), Is.True);
    }

    [Test]
    public void Trace_invalid_jump_exception()
    {
        byte[] code = Prepare.EvmCode
            .PushData(255)
            .Op(Instruction.JUMP)
            .Done;

        GethLikeTxTrace trace = ExecuteAndTrace(1L, 21000L + 19000L, code);

        Assert.That(trace.Entries.Any(static e => e.Error is not null), Is.True);
    }

    [Test]
    public void Trace_invalid_jumpi_exception()
    {
        byte[] code = Prepare.EvmCode
            .PushData(1)
            .PushData(255)
            .Op(Instruction.JUMPI)
            .Done;

        GethLikeTxTrace trace = ExecuteAndTrace(1L, 21000L + 19000L, code);

        Assert.That(trace.Entries.Any(static e => e.Error is not null), Is.True);
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

        AssertFirstPushTrace(trace);
    }

    private static void AssertFirstPushTrace(GethLikeTxTrace trace)
    {
        Assert.That(trace.Entries.Count, Is.EqualTo(5), "number of entries");
        GethTxTraceEntry entry = trace.Entries[1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.Depth, Is.EqualTo(1), nameof(entry.Depth));
            Assert.That(entry.Gas, Is.EqualTo(79000 - GasCostOf.VeryLow), nameof(entry.Gas));
            Assert.That(entry.GasCost, Is.EqualTo(GasCostOf.VeryLow), nameof(entry.GasCost));
            Assert.That(entry.MemoryWordCount(), Is.EqualTo(0), nameof(entry.Memory));
            Assert.That(entry.StackWordCount(), Is.EqualTo(1), nameof(entry.Stack));
            Assert.That(entry.Storage, Is.Null, nameof(entry.Storage));
            Assert.That(trace.Entries[4].Opcode, Is.EqualTo("SSTORE"), "SSTORE opcode");
            Assert.That(entry.ProgramCounter, Is.EqualTo(2), nameof(entry.ProgramCounter));
            Assert.That(entry.Opcode, Is.EqualTo("PUSH1"), nameof(entry.Opcode));
        }

        // Storage is populated lazily during serialization; verify via JSON.
        using JsonDocument doc = JsonDocument.Parse(new EthereumJsonSerializer().Serialize(trace));
        JsonElement sstoreEntry = doc.RootElement.GetProperty("structLogs")[4];
        JsonElement storage = sstoreEntry.GetProperty("storage");
        const string zero32 = "0x0000000000000000000000000000000000000000000000000000000000000000";
        Assert.That(storage.EnumerateObject().Count(), Is.EqualTo(1), "SSTORE storage has one slot");
        Assert.That(storage.GetProperty(zero32).GetString(), Is.EqualTo(zero32), "SSTORE storage[0x0]=0x0");
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SReset), "gas");
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(new byte[] { 0 }), "storage");
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SSet), "gas");
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(new byte[] { 1 }), "storage");
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SSet), "gas");
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(new byte[] { 1 }), "storage");
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.SSet + GasCostOf.Exp + GasCostOf.ExpByteEip160), "gas");
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(BigInteger.Pow(2, 160).ToBigEndianByteArray()), "storage");
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.Exp + GasCostOf.SSet), "gas");
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(BigInteger.One.ToBigEndianByteArray()), "storage");
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.Exp + GasCostOf.ExpByteEip160 + GasCostOf.SReset), "gas");
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(BigInteger.Zero.ToBigEndianByteArray()), "storage");
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.Exp + GasCostOf.ExpByteEip160 + GasCostOf.SSet), "gas");
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(BigInteger.One.ToBigEndianByteArray()), "storage");
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 4 + GasCostOf.SReset), "gas");
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(new byte[] { 0 }), "storage");
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.SSet), "gas");
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo((BigInteger.Pow(2, 256) - 1).ToBigEndianByteArray()), "storage");
        }
    }

    [Test]
    public void Or_0_0()
    {
        TestAllTracerWithOutput receipt = Execute((MainnetSpecProvider.ByzantiumBlockNumber, null),
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.OR,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 4 + GasCostOf.SReset), "gas");
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(BigInteger.Zero.ToBigEndianByteArray()), "storage");
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 2 + GasCostOf.SReset), "gas");
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(BigInteger.Zero.ToBigEndianByteArray()), "storage");
        }
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
        GethLikeTxTrace traces = Execute(new GethLikeTxMemoryTracer(Build.A.Transaction.TestObject, GethTraceOptions.Default), code, MainnetSpecProvider.CancunActivation).BuildResult();

        Assert.That(traces.Entries[^2].GasCost, Is.EqualTo(GasCostOf.VeryLow + GasCostOf.VeryLow * (ulong)((data.Length + 31) / 32) + GasCostOf.Memory * 0UL), "gas");
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
            new GethLikeTxMemoryTracer(Build.A.Transaction.TestObject, GethTraceOptions.Default with { EnableMemory = true }),
            bytecode,
            MainnetSpecProvider.CancunActivation)
            .BuildResult();

        UInt256 copied = traces.Entries.Last().GetMemoryWord(0);
        UInt256 origin = traces.Entries.Last().GetMemoryWord(1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(traces.Entries[^2].GasCost, Is.EqualTo(GasCostOf.VeryLow + GasCostOf.VeryLow * (ulong)((data.Length + 31) / 32) + GasCostOf.Memory * 1UL), "gas");
            Assert.That(origin, Is.EqualTo(copied));
        }
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
            new GethLikeTxMemoryTracer(Build.A.Transaction.TestObject, GethTraceOptions.Default with { EnableMemory = true }),
            bytecode,
            MainnetSpecProvider.CancunActivation)
            .BuildResult();

        UInt256 result = traces.Entries.Last().GetMemoryWord(0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(traces.Entries[^2].GasCost, Is.EqualTo(GasCostOf.VeryLow + GasCostOf.VeryLow * (ulong)(SLICE_SIZE + 31) / 32), "gas");
            Assert.That(result, Is.EqualTo(new UInt256(Bytes.FromHexString("0x0101020304050607080000000000000000000000000000000000000000000000"), isBigEndian: true)), "memory state");
        }
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
            new GethLikeTxMemoryTracer(Build.A.Transaction.TestObject, GethTraceOptions.Default with { EnableMemory = true }),
            bytecode,
            MainnetSpecProvider.CancunActivation)
            .BuildResult();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(traces.Entries[^2].GasCost, Is.EqualTo(GasCostOf.VeryLow + GasCostOf.VeryLow * (ulong)((data.Length + 31) / 32)), "gas");
            Assert.That(traces.Entries.Last().MemoryWordCount(), Is.EqualTo(1));
        }
    }

    [Test]
    public void MCopy_zero_length_does_not_validate_offsets()
    {
        byte[] bytecode = Prepare.EvmCode
            .MCOPY(UInt256.MaxValue, UInt256.MaxValue, UInt256.Zero)
            .STOP()
            .Done;

        TestAllTracerWithOutput receipt = Execute(MainnetSpecProvider.CancunActivation, bytecode);

        Assert.That(receipt.Error, Is.Null);
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
            new GethLikeTxMemoryTracer(Build.A.Transaction.TestObject, GethTraceOptions.Default with { EnableMemory = true }),
            bytecode,
            MainnetSpecProvider.CancunActivation)
            .BuildResult();

        UInt256 result = traces.Entries.Last().GetMemoryWord(0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(traces.Entries[^2].GasCost, Is.EqualTo(GasCostOf.VeryLow + GasCostOf.VeryLow * (ulong)(SLICE_SIZE + 31) / 32), "gas");
            Assert.That(result, Is.EqualTo(new UInt256(Bytes.FromHexString("0x0102030405060708080000000000000000000000000000000000000000000000"), isBigEndian: true)), "memory state");
        }
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

        // Raw revert bytes without an Error(string) selector — GetErrorMessage returns null,
        // so Error falls back to the Revert sentinel.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.Error, Is.EqualTo(Nethermind.Evm.TransactionSubstate.Revert));
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + 20024));
        }
    }

    private static readonly TestCaseData[] TopLevelOutputCases =
    [
        new TestCaseData((byte[])[0xde, 0xad, 0xbe, 0xef]).SetName("Sub_word_output"),
        new TestCaseData(Bytes.FromHexString("0x00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff0123456789abcdef")).SetName("Multi_word_output"),
    ];

    // Regression cover for the returndata copy-elision in the transaction processor: the bytes handed to the
    // receipt tracer must equal the top-level RETURN / REVERT / precompile output, whether the backing array is
    // forwarded directly or copied.
    [TestCaseSource(nameof(TopLevelOutputCases))]
    public void Return_output_reaches_receipt_tracer_verbatim(byte[] data)
    {
        byte[] code = Prepare.EvmCode
            .StoreDataInMemory(0, data)
            .Return(data.Length, 0)
            .Done;

        TestAllTracerWithOutput receipt = Execute(code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(receipt.ReturnValue, Is.EqualTo(data));
        }
    }

    [TestCaseSource(nameof(TopLevelOutputCases))]
    public void Revert_output_reaches_receipt_tracer_verbatim(byte[] data)
    {
        byte[] code = Prepare.EvmCode
            .StoreDataInMemory(0, data)
            .Revert(data.Length, 0)
            .Done;

        TestAllTracerWithOutput receipt = Execute(code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Failure));
            Assert.That(receipt.ReturnValue, Is.EqualTo(data));
        }
    }

    [Test]
    public void Empty_return_yields_empty_receipt_output()
    {
        TestAllTracerWithOutput receipt = Execute(Prepare.EvmCode.Return(0, 0).Done);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(receipt.ReturnValue, Is.Empty);
        }
    }

    // Top-level call straight to a precompile exercises the precompile output path, where the backing array may be
    // a whole array that is forwarded without copying.
    [Test]
    public void Top_level_precompile_output_reaches_receipt_tracer_verbatim()
    {
        byte[] input = Bytes.FromHexString("0x00112233445566778899aabbccddeeff");
        EthereumEcdsa ecdsa = new(SpecProvider.ChainId);
        Transaction tx = Build.A.Transaction
            .WithTo(IdentityPrecompile.Address)
            .WithData(input)
            .WithGasLimit(100_000)
            .SignedAndResolved(ecdsa, SenderKey)
            .TestObject;

        TestAllTracerWithOutput receipt = Execute(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(receipt.ReturnValue, Is.EqualTo(input));
        }
    }
}
