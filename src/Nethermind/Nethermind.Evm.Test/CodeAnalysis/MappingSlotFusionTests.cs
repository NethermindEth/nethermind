// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test.CodeAnalysis;

/// <summary>
/// Runs the same code twice — with instruction tracing, which forces the dispatch loop, and without,
/// which lets the scratch-space hash fuse — and requires the two to agree.
/// </summary>
[TestFixture]
public class MappingSlotFusionTests : VirtualMachineTestsBase
{
    protected override ForkActivation Activation => MainnetSpecProvider.CancunActivation;

    [Test]
    public void Finds_the_scratch_hash_in_both_zero_push_forms()
    {
        Assert.That(new CodeInfo(MappingSlot(key: 7, slot: 3, push0: false)).Fusion, Is.Not.Null);
        Assert.That(new CodeInfo(MappingSlot(key: 7, slot: 3, push0: true)).Fusion, Is.Not.Null);
    }

    [Test]
    public void Finds_nothing_in_code_without_the_run() =>
        Assert.That(new CodeInfo(Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.MSTORE).Done).Fusion, Is.Null);

    /// <summary>A PUSH immediate that happens to spell the run must not be matched as one.</summary>
    [Test]
    public void Finds_nothing_when_the_run_lies_inside_push_data()
    {
        byte[] window = [0x52, 0x60, 0x20, 0x52, 0x60, 0x40, 0x5f, 0x20];
        byte[] code = [(byte)Instruction.PUSH32, .. window, .. new byte[24], (byte)Instruction.STOP];

        Assert.That(new CodeInfo(code).Fusion, Is.Null);
    }

    [TestCase(false, TestName = "Zero pushed with PUSH1 0x00")]
    [TestCase(true, TestName = "Zero pushed with PUSH0")]
    public void Matches_the_dispatch_loop_for_a_mapping_slot(bool push0) =>
        AssertBothPathsAgree(MappingSlot(key: 0x1234, slot: 5, push0));

    [TestCase(0UL)]
    [TestCase(1UL)]
    [TestCase(ulong.MaxValue)]
    public void Matches_the_dispatch_loop_across_keys(ulong key) =>
        AssertBothPathsAgree(MappingSlot(key, slot: 2, push0: false));

    /// <summary>Two mapping accesses in a row, so the second fuses from a memory the first already grew.</summary>
    [Test]
    public void Matches_the_dispatch_loop_for_consecutive_mapping_slots()
    {
        List<byte> code = [];
        code.AddRange(MappingSlotCore(key: 9, slot: 1, push0: false));
        code.Add((byte)Instruction.POP);
        code.AddRange(MappingSlotCore(key: 11, slot: 2, push0: true));
        code.AddRange(Return32);

        AssertBothPathsAgree([.. code]);
    }

    /// <summary>The run must not fire when the stack cannot feed it; the loop reaches the same underflow.</summary>
    [Test]
    public void Matches_the_dispatch_loop_when_the_stack_is_too_shallow() =>
        AssertBothPathsAgree([(byte)Instruction.PUSH1, 0x00, (byte)Instruction.PUSH1, 0x00, .. ScratchHash(push0: false)]);

    /// <summary>Gas runs out partway through the run, which must halt exactly where the loop does.</summary>
    [TestCase(21_060UL)]
    [TestCase(21_070UL)]
    [TestCase(21_080UL)]
    public void Matches_the_dispatch_loop_when_gas_runs_out_inside_the_run(ulong gasLimit) =>
        AssertBothPathsAgree(MappingSlot(key: 4, slot: 6, push0: false), gasLimit);

    /// <summary>PUSH0 only exists from Shanghai, so before it the run must stay unfused.</summary>
    [Test]
    public void Matches_the_dispatch_loop_for_a_push0_run_before_push0_exists() =>
        AssertBothPathsAgree(MappingSlot(key: 4, slot: 6, push0: true), 100_000UL, MainnetSpecProvider.ParisBlockNumber);

    /// <summary>The leading MSTORE writes wherever the stack says, which the run must honour.</summary>
    [Test]
    public void Matches_the_dispatch_loop_when_the_first_store_is_not_at_scratch()
    {
        List<byte> code =
        [
            (byte)Instruction.PUSH1, 0x2a,
            (byte)Instruction.PUSH1, 0x80,
            (byte)Instruction.PUSH1, 0x07,
            .. ScratchHash(push0: false),
            .. Return32,
        ];

        AssertBothPathsAgree([.. code]);
    }

    private static ReadOnlySpan<byte> Return32 =>
        [(byte)Instruction.PUSH1, 0x00, (byte)Instruction.MSTORE,
         (byte)Instruction.PUSH1, 0x20, (byte)Instruction.PUSH1, 0x00, (byte)Instruction.RETURN];

    /// <summary>The fusable run itself: MSTORE, PUSH1 0x20, MSTORE, PUSH1 0x40, zero, KECCAK256.</summary>
    private static byte[] ScratchHash(bool push0) =>
    [
        (byte)Instruction.MSTORE,
        (byte)Instruction.PUSH1, 0x20, (byte)Instruction.MSTORE,
        (byte)Instruction.PUSH1, 0x40,
        .. push0 ? new byte[] { (byte)Instruction.PUSH0 } : [(byte)Instruction.PUSH1, 0x00],
        (byte)Instruction.KECCAK256,
    ];

    /// <summary>Pushes the slot and key the way a mapping access leaves them, then hashes the scratch space.</summary>
    private static byte[] MappingSlotCore(ulong key, byte slot, bool push0) =>
    [
        (byte)Instruction.PUSH1, slot,
        .. PushUInt64(key),
        (byte)Instruction.PUSH1, 0x00,
        .. ScratchHash(push0),
    ];

    private static byte[] MappingSlot(ulong key, byte slot, bool push0) =>
        [.. MappingSlotCore(key, slot, push0), .. Return32];

    private static byte[] PushUInt64(ulong value)
    {
        byte[] bytes = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return [(byte)Instruction.PUSH8, .. bytes];
    }

    private void AssertBothPathsAgree(byte[] code, ulong gasLimit = 100_000UL, ulong? blockNumber = null)
    {
        ForkActivation activation = blockNumber is { } number ? new ForkActivation(number) : Activation;

        CallOutputTracer fused = new();
        Run(code, gasLimit, activation, fused);

        InstructionTracingCallOutputTracer dispatchLoop = new();
        Run(code, gasLimit, activation, dispatchLoop);

        Assert.Multiple(() =>
        {
            Assert.That(fused.GasSpent, Is.EqualTo(dispatchLoop.GasSpent), "gas spent");
            Assert.That(fused.StatusCode, Is.EqualTo(dispatchLoop.StatusCode), "status code");
            Assert.That(fused.ReturnValue, Is.EqualTo(dispatchLoop.ReturnValue), "return value");
            Assert.That(fused.Error, Is.EqualTo(dispatchLoop.Error), "error");
        });
    }

    private void Run(byte[] code, ulong gasLimit, ForkActivation activation, ITxTracer tracer)
    {
        TearDown();
        Setup();

        (Block block, Transaction transaction) = PrepareTx(activation, gasLimit, code, [], UInt256.Zero);
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
    }

    /// <summary>Records the same result as <see cref="CallOutputTracer"/> while forcing the dispatch loop.</summary>
    private sealed class InstructionTracingCallOutputTracer : CallOutputTracer
    {
        public override bool IsTracingInstructions => true;
    }
}
