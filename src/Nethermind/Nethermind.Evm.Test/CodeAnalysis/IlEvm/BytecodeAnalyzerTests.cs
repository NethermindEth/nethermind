// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis.IlEvm;
using NUnit.Framework;

namespace Nethermind.Evm.Test.CodeAnalysis.IlEvm;

[TestFixture]
public class BytecodeAnalyzerTests
{
    private static readonly IReleaseSpec IstanbulSpec = Nethermind.Specs.Forks.Istanbul.Instance;
    private static readonly IReleaseSpec FrontierSpec = Nethermind.Specs.Forks.Frontier.Instance;

    [Test]
    public void Analyze_StraightLineArithmetic_ProducesSingleCompilableBlock()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH1, 0x01,
            (byte)Instruction.PUSH1, 0x02,
            (byte)Instruction.ADD,
            (byte)Instruction.STOP,
        ];

        AnalyzedCode analyzed = BytecodeAnalyzer.Analyze(code, IstanbulSpec);

        Assert.That(analyzed.BlockCount, Is.EqualTo(1), "straight-line code with one terminator is one block");
        BasicBlock block = analyzed.Blocks[0];
        Assert.That(block.Start, Is.EqualTo(0), "the block starts at the first opcode");
        Assert.That(block.End, Is.EqualTo(code.Length), "the block covers the whole code");
        Assert.That(block.IsCompilable, Is.True, "PUSH/ADD/STOP are all v1 opcodes");
        Assert.That(block.StaticGas, Is.EqualTo(9), "PUSH1(3) + PUSH1(3) + ADD(3) + STOP(0)");
        Assert.That(block.StackRequired, Is.EqualTo(0), "the block produces its own operands");
        Assert.That(block.StackMaxGrowth, Is.EqualTo(2), "two pushes precede the ADD");
        Assert.That(block.StackDelta, Is.EqualTo(1), "two pushes minus one two-operand op leaves one value");
        Assert.That(block.Flags.HasFlag(BasicBlockFlags.EndsWithTerminator), Is.True, "the block ends with STOP");
    }

    [Test]
    public void Analyze_JumpAndJumpDest_SplitsAtControlFlowBoundaries()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH1, 0x03,
            (byte)Instruction.JUMP,      // pc 2
            (byte)Instruction.JUMPDEST,  // pc 3
            (byte)Instruction.STOP,      // pc 4
        ];

        AnalyzedCode analyzed = BytecodeAnalyzer.Analyze(code, IstanbulSpec);

        Assert.That(analyzed.BlockCount, Is.EqualTo(2), "JUMP ends the first block and JUMPDEST leads the second");

        BasicBlock first = analyzed.Blocks[0];
        Assert.That(first.Start, Is.EqualTo(0), "precondition: first block starts at 0");
        Assert.That(first.End, Is.EqualTo(3), "the JUMP at pc 2 is the last opcode of the first block");
        Assert.That(first.StaticGas, Is.EqualTo(11), "PUSH1(3) + JUMP(8)");
        Assert.That(first.Flags.HasFlag(BasicBlockFlags.EndsWithJump), Is.True, "the block ends with JUMP");

        BasicBlock second = analyzed.Blocks[1];
        Assert.That(second.Start, Is.EqualTo(3), "JUMPDEST is a leader");
        Assert.That(second.StaticGas, Is.EqualTo(1), "JUMPDEST(1) + STOP(0)");
        Assert.That(second.Flags.HasFlag(BasicBlockFlags.StartsWithJumpDest), Is.True, "the block begins at a JUMPDEST");

        Assert.That(analyzed.TryGetBlockStartingAt(3, out BasicBlock found), Is.True, "jump targets must resolve to their block");
        Assert.That(found.Start, Is.EqualTo(3), "the lookup must return the block led by pc 3");
        Assert.That(analyzed.TryGetBlockStartingAt(1, out _), Is.False, "pc 1 is push data, not a leader");
    }

    [Test]
    public void Analyze_JumpDestByteInsidePushData_IsNotALeader()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH2, (byte)Instruction.JUMPDEST, (byte)Instruction.JUMPDEST,
            (byte)Instruction.STOP,
        ];

        AnalyzedCode analyzed = BytecodeAnalyzer.Analyze(code, IstanbulSpec);

        Assert.That(analyzed.BlockCount, Is.EqualTo(1), "0x5B bytes inside PUSH immediates are data, not opcodes");
        Assert.That(analyzed.Blocks[0].StaticGas, Is.EqualTo(3), "PUSH2(3) + STOP(0)");
    }

    [Test]
    public void Analyze_NonV1Opcode_IsIsolatedInInterpreterOnlyBlock()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH1, 0x00,
            (byte)Instruction.CALL,      // pc 2 — interpreter-only (calls never compile)
            (byte)Instruction.PUSH1, 0x01,
            (byte)Instruction.ADD,
            (byte)Instruction.STOP,
        ];

        AnalyzedCode analyzed = BytecodeAnalyzer.Analyze(code, IstanbulSpec);

        Assert.That(analyzed.BlockCount, Is.EqualTo(3), "classification changes cut blocks around the SLOAD");
        Assert.That(analyzed.Blocks[0].IsCompilable, Is.True, "the leading PUSH1 is v1");
        Assert.That(analyzed.Blocks[1].IsCompilable, Is.False, "SLOAD is interpreter-only in v1");
        Assert.That(analyzed.Blocks[1].Start, Is.EqualTo(2), "the interpreter-only block holds exactly the SLOAD");
        Assert.That(analyzed.Blocks[1].End, Is.EqualTo(3), "SLOAD has no immediates");
        Assert.That(analyzed.Blocks[2].IsCompilable, Is.True, "the trailing arithmetic is v1");
        Assert.That(analyzed.Blocks[2].StaticGas, Is.EqualTo(6), "PUSH1(3) + ADD(3) + STOP(0)");
    }

    [Test]
    public void Analyze_TruncatedPush_EndsBlockAtCodeEnd()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH32, 0xAA, 0xBB,
        ];

        AnalyzedCode analyzed = BytecodeAnalyzer.Analyze(code, IstanbulSpec);

        Assert.That(analyzed.BlockCount, Is.EqualTo(1), "a truncated PUSH still forms a block");
        Assert.That(analyzed.Blocks[0].End, Is.EqualTo(code.Length), "the block cannot extend past the code");
        Assert.That(analyzed.Blocks[0].IsCompilable, Is.True, "a truncated PUSH executes with zero padding and stays v1");
        Assert.That(analyzed.Blocks[0].StackDelta, Is.EqualTo(1), "the truncated PUSH still pushes one value");
    }

    [TestCase(true, 1, TestName = "Istanbul_ShlIsV1_SingleBlock")]
    [TestCase(false, 3, TestName = "Frontier_ShlIsInterpreterOnly_ThreeBlocks")]
    public void Analyze_ShiftOpcode_CompilableOnlyWhenSpecEnablesShifts(bool useIstanbul, int expectedBlocks)
    {
        byte[] code =
        [
            (byte)Instruction.PUSH1, 0x01,
            (byte)Instruction.PUSH1, 0x01,
            (byte)Instruction.SHL,
            (byte)Instruction.STOP,
        ];

        IReleaseSpec spec = useIstanbul ? IstanbulSpec : FrontierSpec;
        AnalyzedCode analyzed = BytecodeAnalyzer.Analyze(code, spec);

        Assert.That(analyzed.BlockCount, Is.EqualTo(expectedBlocks), "SHL exists only when the spec enables shift opcodes");
    }

    [Test]
    public void Analyze_StackEffects_TrackRequiredDepthAndDelta()
    {
        byte[] code =
        [
            (byte)Instruction.SWAP1,
            (byte)Instruction.POP,
            (byte)Instruction.STOP,
        ];

        AnalyzedCode analyzed = BytecodeAnalyzer.Analyze(code, IstanbulSpec);

        BasicBlock block = analyzed.Blocks[0];
        Assert.That(block.StackRequired, Is.EqualTo(2), "SWAP1 needs two values on entry");
        Assert.That(block.StackMaxGrowth, Is.EqualTo(0), "nothing grows the stack above the entry depth");
        Assert.That(block.StackDelta, Is.EqualTo(-1), "the POP removes one value net");
    }

    [Test]
    public void Analyze_MemoryOpcode_MarksDynamicGas()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH1, 0x00,
            (byte)Instruction.MLOAD,
            (byte)Instruction.STOP,
        ];

        AnalyzedCode analyzed = BytecodeAnalyzer.Analyze(code, IstanbulSpec);

        Assert.That(analyzed.Blocks[0].IsCompilable, Is.True, "MLOAD belongs to the v1 subset");
        Assert.That(analyzed.Blocks[0].Flags.HasFlag(BasicBlockFlags.HasDynamicGas), Is.True, "memory expansion gas is dynamic");
    }

    [Test]
    public void Analyze_EmptyCode_ProducesNoBlocks()
    {
        AnalyzedCode analyzed = BytecodeAnalyzer.Analyze([], IstanbulSpec);

        Assert.That(analyzed.BlockCount, Is.EqualTo(0), "empty code has nothing to analyze");
    }
}
