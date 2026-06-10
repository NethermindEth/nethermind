// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.TransactionProcessing;
using NUnit.Framework;

namespace Nethermind.Evm.Test.CodeAnalysis;

[TestFixture]
public class InstructionStreamTests
{
    [Test]
    public void TryBuild_StraightLineArithmetic_SumsTheBlockGasOnce()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH1, 0x01,
            (byte)Instruction.PUSH1, 0x02,
            (byte)Instruction.ADD,
            (byte)Instruction.STOP,
        ];

        InstructionStream stream = InstructionStream.TryBuild(code)!;

        Assert.That(stream, Is.Not.Null);
        Assert.That(stream.BlockGas, Has.Length.EqualTo(1));
        Assert.That(stream.BlockGas[0], Is.EqualTo(3 * GasCostOf.VeryLow),
            "two pushes and an add are one block charged as a single sum");
        Assert.That(stream.Ops[0].Kind, Is.EqualTo(StreamOpKind.BlockStart));
        Assert.That(stream.Ops[^1].Kind, Is.EqualTo(StreamOpKind.Boundary),
            "STOP is not a static-cost op and must run the standard handler");
    }

    [Test]
    public void TryBuild_Jumpdest_GetsItsOwnSoloBlock()
    {
        byte[] code =
        [
            (byte)Instruction.JUMPDEST,
            (byte)Instruction.PUSH1, 0x01,
            (byte)Instruction.POP,
        ];

        InstructionStream stream = InstructionStream.TryBuild(code)!;

        Assert.That(stream.BlockGas, Has.Length.EqualTo(2),
            "a fused PUSH2+JUMP lands one past the JUMPDEST, so the ops after it need a separately charged block");
        Assert.That(stream.BlockGas[0], Is.EqualTo(GasCostOf.JumpDest));
        Assert.That(stream.BlockGas[1], Is.EqualTo(GasCostOf.VeryLow + GasCostOf.Base));
    }

    [Test]
    public void TryBuild_JumpAndPush2_AreJumpClass()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH2, 0x00, 0x05,
            (byte)Instruction.JUMP,
            (byte)Instruction.STOP,
            (byte)Instruction.JUMPDEST,
        ];

        InstructionStream stream = InstructionStream.TryBuild(code)!;

        Assert.That(stream.Ops[0].Kind, Is.EqualTo(StreamOpKind.JumpClass), "PUSH2 keeps the fused PUSH2+JUMP handler");
        Assert.That(stream.Ops[1].Kind, Is.EqualTo(StreamOpKind.JumpClass), "JUMP recomputes the entry index from the landing pc");
    }

    [Test]
    public void TryBuild_PcToEntry_MapsInstructionStartsAndRejectsImmediates()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH3, 0x01, 0x02, 0x03,
            (byte)Instruction.ADD,
        ];

        InstructionStream stream = InstructionStream.TryBuild(code)!;

        Assert.That(stream.PcToEntry[0], Is.EqualTo(0), "PUSH3 opens the block, so its pc maps to the BlockStart entry");
        Assert.That(stream.PcToEntry[1], Is.EqualTo(InstructionStream.InvalidEntry));
        Assert.That(stream.PcToEntry[2], Is.EqualTo(InstructionStream.InvalidEntry));
        Assert.That(stream.PcToEntry[3], Is.EqualTo(InstructionStream.InvalidEntry));
        Assert.That(stream.PcToEntry[4], Is.EqualTo(2), "ADD continues the open block as a plain entry");
        Assert.That(stream.PcToEntry[5], Is.EqualTo(stream.Ops.Length), "pc one past the end terminates the stream loop");
    }

    [Test]
    public void TryBuild_EmptyCode_ReturnsNull()
        => Assert.That(InstructionStream.TryBuild(ReadOnlySpan<byte>.Empty), Is.Null);

    [TestCase(Instruction.ADD, GasCostOf.VeryLow, TestName = "Add")]
    [TestCase(Instruction.MUL, GasCostOf.Low, TestName = "Mul")]
    [TestCase(Instruction.SDIV, GasCostOf.Low, TestName = "SDiv")]
    [TestCase(Instruction.POP, GasCostOf.Base, TestName = "Pop")]
    [TestCase(Instruction.PUSH0, GasCostOf.Base, TestName = "Push0")]
    [TestCase(Instruction.SHL, GasCostOf.VeryLow, TestName = "Shl")]
    [TestCase(Instruction.DUP8, GasCostOf.VeryLow, TestName = "Dup8")]
    [TestCase(Instruction.SWAP8, GasCostOf.VeryLow, TestName = "Swap8")]
    public void TryGetInBlockCost_ForStaticCostOp_MatchesGasCostOf(Instruction instruction, long expectedCost)
    {
        Assert.That(InstructionStream.TryGetInBlockCost(instruction, out long cost), Is.True);
        Assert.That(cost, Is.EqualTo(expectedCost), "block sums diverging from GasCostOf is a consensus bug");
    }

    [TestCase(Instruction.PUSH2, TestName = "Push2_IsJumpClassNotInBlock")]
    [TestCase(Instruction.DUP9, TestName = "Dup9_OutsideExecutorSwitch")]
    [TestCase(Instruction.SWAP9, TestName = "Swap9_OutsideExecutorSwitch")]
    [TestCase(Instruction.PUSH5, TestName = "Push5_OutsideExecutorSwitch")]
    [TestCase(Instruction.MLOAD, TestName = "MLoad_DynamicMemoryGas")]
    [TestCase(Instruction.SLOAD, TestName = "SLoad_DynamicAccessGas")]
    [TestCase(Instruction.GAS, TestName = "Gas_MustObserveExactRemaining")]
    [TestCase(Instruction.EXP, TestName = "Exp_DynamicGas")]
    public void TryGetInBlockCost_ForExcludedOp_ReturnsFalse(Instruction instruction)
        => Assert.That(InstructionStream.TryGetInBlockCost(instruction, out _), Is.False);
}

[TestFixture]
public class StreamInterpreterDifferentialTests : VirtualMachineTestsBase
{
    private static readonly byte[] s_arithmeticChain = Prepare.EvmCode
        .PushData(7).PushData(5).Op(Instruction.ADD)
        .PushData(3).Op(Instruction.MUL)
        .PushData(2).Op(Instruction.SWAP1).Op(Instruction.DIV)
        .Op(Instruction.DUP1).Op(Instruction.EQ)
        .Op(Instruction.ISZERO)
        .Op(Instruction.POP)
        .Op(Instruction.STOP)
        .Done;

    private static readonly byte[] s_jumpLoop = Prepare.EvmCode
        .PushData(5)                                  // counter
        .Op(Instruction.JUMPDEST)                     // pc 2: loop head
        .PushData(1).Op(Instruction.SWAP1).Op(Instruction.SUB)
        .Op(Instruction.DUP1)
        .PushData(2).Op(Instruction.JUMPI)            // loop while counter != 0
        .Op(Instruction.STOP)
        .Done;

    private static readonly byte[] s_storeAndReturn = Prepare.EvmCode
        .PushData(42).PushData(0).Op(Instruction.MSTORE)
        .PushData(32).PushData(0).Op(Instruction.RETURN)
        .Done;

    private static readonly byte[] s_stackUnderflow = Prepare.EvmCode
        .Op(Instruction.ADD)
        .Done;

    public static IEnumerable<TestCaseData> DifferentialCases()
    {
        yield return new TestCaseData(s_arithmeticChain) { TestName = "ArithmeticChain" };
        yield return new TestCaseData(s_jumpLoop) { TestName = "JumpLoopWithFusedPush" };
        yield return new TestCaseData(s_storeAndReturn) { TestName = "MemoryBoundaryOpsAndReturn" };
        yield return new TestCaseData(s_stackUnderflow) { TestName = "StackUnderflowFailure" };
    }

    [TestCaseSource(nameof(DifferentialCases))]
    public void StreamInterpreter_ComparedToByteCodeLoop_IsObservablyIdentical(byte[] code)
    {
        ReceiptCaptureTracer baseline = RunWithInterpreter(code, useStream: false);

        long framesBefore = StreamInterpreter.FramesExecuted;
        ReceiptCaptureTracer streamed = RunWithInterpreter(code, useStream: true);

        Assert.That(StreamInterpreter.FramesExecuted, Is.GreaterThan(framesBefore),
            "the stream interpreter did not engage — this differential proved nothing");
        Assert.That(streamed.StatusCode, Is.EqualTo(baseline.StatusCode), "success/failure must match");
        Assert.That(streamed.GasSpent, Is.EqualTo(baseline.GasSpent), "gas must match to the unit");
        Assert.That(streamed.Output, Is.EqualTo(baseline.Output), "return data must match");
    }

    private ReceiptCaptureTracer RunWithInterpreter(byte[] code, bool useStream)
    {
        bool enabledBefore = StreamInterpreter.Enabled;
        StreamInterpreter.Enabled = useStream;
        try
        {
            return Execute(new ReceiptCaptureTracer(), code);
        }
        finally
        {
            StreamInterpreter.Enabled = enabledBefore;
        }
    }

    private sealed class ReceiptCaptureTracer : Evm.Tracing.TxTracer
    {
        public byte StatusCode { get; private set; }
        public long GasSpent { get; private set; }
        public byte[] Output { get; private set; } = [];

        public override bool IsTracingReceipt => true;

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
        {
            StatusCode = Evm.StatusCode.Success;
            GasSpent = gasSpent.SpentGas;
            Output = output;
        }

        public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
        {
            StatusCode = Evm.StatusCode.Failure;
            GasSpent = gasSpent.SpentGas;
            Output = output;
        }
    }
}
