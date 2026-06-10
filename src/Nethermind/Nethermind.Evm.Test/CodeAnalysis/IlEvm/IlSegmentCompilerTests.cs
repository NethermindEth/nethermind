// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis.IlEvm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test.CodeAnalysis.IlEvm;

[TestFixture]
[NonParallelizable]
public class IlSegmentCompilerTests
{
    private static readonly IReleaseSpec Spec = Nethermind.Specs.Forks.Istanbul.Instance;

    private int _minimumOpsBackup;
    private int _boundaryFactorBackup;

    [SetUp]
    public void SetUp()
    {
        _minimumOpsBackup = IlSegmentCompiler.MinimumPrefixOps;
        _boundaryFactorBackup = IlSegmentCompiler.BoundaryCostFactor;
        // These tests target emission semantics on intentionally tiny segments; relax the
        // production profitability gate so they compile.
        IlSegmentCompiler.MinimumPrefixOps = 1;
        IlSegmentCompiler.BoundaryCostFactor = 0;
    }

    [TearDown]
    public void TearDown()
    {
        IlSegmentCompiler.MinimumPrefixOps = _minimumOpsBackup;
        IlSegmentCompiler.BoundaryCostFactor = _boundaryFactorBackup;
    }

    [Test]
    public void Compile_PushAddChain_ComputesResultAndAdvancesPc()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH1, 5,
            (byte)Instruction.PUSH1, 7,
            (byte)Instruction.ADD,
            (byte)Instruction.STOP,
        ];

        SegmentRun run = Run(code, [], gasLimit: 100);

        Assert.That(run.Result, Is.EqualTo(EvmExceptionType.None), "the chain executes cleanly");
        Assert.That(run.StackBottomFirst, Is.EqualTo(new UInt256[] { 12 }), "5 + 7 = 12");
        Assert.That(run.GasLeft, Is.EqualTo(91), "PUSH1(3) + PUSH1(3) + ADD(3) consumed");
        Assert.That(run.ProgramCounter, Is.EqualTo(5), "the segment exits at the STOP for the interpreter to execute");
        Assert.That(run.Segment.OpCount, Is.EqualTo(3), "STOP is not emittable and ends the prefix");
    }

    [Test]
    public void Compile_SwapAndSub_PreservesEvmOperandOrder()
    {
        byte[] code =
        [
            (byte)Instruction.SWAP1,
            (byte)Instruction.SUB,
            (byte)Instruction.DUP1,
            (byte)Instruction.STOP,
        ];

        SegmentRun run = Run(code, [10, 3], gasLimit: 100);

        Assert.That(run.Result, Is.EqualTo(EvmExceptionType.None), "two entry operands are available");
        Assert.That(run.StackBottomFirst, Is.EqualTo(new UInt256[] { 7, 7 }), "SWAP1 makes 10 the top, SUB computes top - second = 10 - 3, DUP1 duplicates");
        Assert.That(run.GasLeft, Is.EqualTo(91), "three VeryLow ops consumed");
    }

    [Test]
    public void Compile_LtThenIsZero_ProducesBooleanWords()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH1, 2,
            (byte)Instruction.PUSH1, 1,
            (byte)Instruction.LT,
            (byte)Instruction.ISZERO,
            (byte)Instruction.STOP,
        ];

        SegmentRun run = Run(code, [], gasLimit: 100);

        Assert.That(run.Result, Is.EqualTo(EvmExceptionType.None), "the comparison chain executes cleanly");
        Assert.That(run.StackBottomFirst, Is.EqualTo(new UInt256[] { 0 }), "LT: top(1) < second(2) is true(1); ISZERO(1) = 0");
        Assert.That(run.GasLeft, Is.EqualTo(88), "four VeryLow ops consumed");
    }

    [Test]
    public void Compile_MulThenDiv_UsesIntegerSemantics()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH1, 6,
            (byte)Instruction.PUSH1, 7,
            (byte)Instruction.MUL,
            (byte)Instruction.PUSH1, 5,
            (byte)Instruction.SWAP1,
            (byte)Instruction.DIV,
            (byte)Instruction.STOP,
        ];

        SegmentRun run = Run(code, [], gasLimit: 100);

        Assert.That(run.Result, Is.EqualTo(EvmExceptionType.None), "the arithmetic chain executes cleanly");
        Assert.That(run.Segment.OpCount, Is.EqualTo(6), "every op before STOP is emittable, including DIV");
        Assert.That(run.StackBottomFirst, Is.EqualTo(new UInt256[] { 8 }), "42 / 5 = 8 in integer division");
    }

    [Test]
    public void Compile_ProductionProfitabilityGate_RejectsBoundaryHeavyAndAcceptsLongChains()
    {
        IlSegmentCompiler.MinimumPrefixOps = 8;
        IlSegmentCompiler.BoundaryCostFactor = 3;

        byte[] shortStackHeavy =
        [
            (byte)Instruction.SWAP1,
            (byte)Instruction.SUB,
            (byte)Instruction.DUP1,
            (byte)Instruction.STOP,
        ];
        AnalyzedCode analyzedShort = BytecodeAnalyzer.Analyze(shortStackHeavy, Spec);
        bool compiledShort = IlSegmentCompiler.TryCompile(shortStackHeavy, analyzedShort.Blocks[0], out _);
        Assert.That(compiledShort, Is.False, "three ops against five boundary conversions is a net loss");

        byte[] longChain = new byte[2 + 6 * 3 + 1];
        int i = 0;
        longChain[i++] = (byte)Instruction.PUSH1;
        longChain[i++] = 9;
        for (int round = 0; round < 6; round++)
        {
            longChain[i++] = (byte)Instruction.PUSH1;
            longChain[i++] = 7;
            longChain[i++] = (byte)Instruction.ADD;
        }
        longChain[i] = (byte)Instruction.STOP;
        AnalyzedCode analyzedLong = BytecodeAnalyzer.Analyze(longChain, Spec);
        bool compiledLong = IlSegmentCompiler.TryCompile(longChain, analyzedLong.Blocks[0], out IlCompiledSegment? segment);
        Assert.That(compiledLong, Is.True, "a 13-op dependence chain with one exit value amortizes its boundary");
        Assert.That(segment!.StackRequired, Is.EqualTo(0), "the chain produces its own operands");
    }

    [Test]
    public void Compile_SegmentMetadata_ExposesDispatchPreconditions()
    {
        byte[] code =
        [
            (byte)Instruction.ADD,        // needs 2 on entry
            (byte)Instruction.DUP1,       // grows above entry-1 by 1
            (byte)Instruction.PUSH1, 7,   // grows by 1 more
            (byte)Instruction.STOP,
        ];

        AnalyzedCode analyzed = BytecodeAnalyzer.Analyze(code, Spec);
        bool compiled = IlSegmentCompiler.TryCompile(code, analyzed.Blocks[0], out IlCompiledSegment? segment);

        Assert.That(compiled, Is.True, "precondition: the prefix must compile");
        Assert.That(segment!.StackRequired, Is.EqualTo(2), "ADD reaches two entries deep");
        Assert.That(segment.StackMaxGrowth, Is.EqualTo(1), "net depth peaks one above entry (-1 after ADD, +1 DUP, +1 PUSH)");
        Assert.That(segment.StaticGas, Is.EqualTo(9), "ADD(3) + DUP1(3) + PUSH1(3)");
        // The dispatch site uses these to fall through to the interpreter for any execution
        // that could fail, so a segment invocation can never produce a halt itself.
    }

    [Test]
    public void Compile_TruncatedPushAtCodeEnd_StopsThePrefixBeforeIt()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH1, 1,
            (byte)Instruction.PUSH1, 2,
            (byte)Instruction.ADD,
            (byte)Instruction.PUSH2, 0xAA, // truncated: one immediate byte missing
        ];

        SegmentRun run = Run(code, [], gasLimit: 100);

        Assert.That(run.Segment.OpCount, Is.EqualTo(3), "the truncated PUSH2 is left to the interpreter");
        Assert.That(run.Segment.ExitPc, Is.EqualTo(5), "the segment exits at the truncated PUSH2");
        Assert.That(run.StackBottomFirst, Is.EqualTo(new UInt256[] { 3 }), "the emitted prefix still computes 1 + 2");
    }

    [Test]
    public void Compile_Push32_DecodesFullWidthBigEndianConstant()
    {
        byte[] immediate = new byte[32];
        for (int i = 0; i < immediate.Length; i++)
            immediate[i] = (byte)(i + 1);
        byte[] code = new byte[1 + 32 + 3];
        code[0] = (byte)Instruction.PUSH32;
        immediate.CopyTo(code, 1);
        code[33] = (byte)Instruction.DUP1;
        code[34] = (byte)Instruction.POP;
        code[35] = (byte)Instruction.STOP;

        SegmentRun run = Run(code, [], gasLimit: 100);

        UInt256 expected = new(immediate, isBigEndian: true);
        Assert.That(run.StackBottomFirst, Is.EqualTo(new[] { expected }), "the constant must round-trip the full 32 bytes big-endian");
    }

    [Test]
    public void Compile_DivisionByZero_PushesZeroLikeTheInterpreter()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH1, 0,   // divisor
            (byte)Instruction.PUSH1, 7,   // dividend (top)
            (byte)Instruction.DIV,
            (byte)Instruction.PUSH1, 0,
            (byte)Instruction.PUSH1, 7,
            (byte)Instruction.MOD,
            (byte)Instruction.STOP,
        ];

        SegmentRun run = Run(code, [], gasLimit: 100);

        Assert.That(run.Result, Is.EqualTo(EvmExceptionType.None), "division by zero must not throw");
        Assert.That(run.StackBottomFirst, Is.EqualTo(new UInt256[] { 0, 0 }), "EVM semantics: x/0 = 0 and x%0 = 0");
    }

    [TestCase((byte)Instruction.SHL, 4, 1, 16ul, TestName = "Shl_Normal")]
    [TestCase((byte)Instruction.SHR, 4, 16, 1ul, TestName = "Shr_Normal")]
    public void Compile_ShiftWithinRange_MatchesEvmSemantics(byte shiftOpcode, byte shiftBy, byte value, ulong expected)
    {
        byte[] code =
        [
            (byte)Instruction.PUSH1, value,
            (byte)Instruction.PUSH1, shiftBy, // shift amount on top
            shiftOpcode,
            (byte)Instruction.STOP,
        ];

        SegmentRun run = Run(code, [], gasLimit: 100);

        Assert.That(run.StackBottomFirst, Is.EqualTo(new UInt256[] { expected }), "shift must use the top operand as the amount");
    }

    [Test]
    public void Compile_ShiftAmountOf256OrMore_YieldsZero()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH1, 0xFF,
            (byte)Instruction.PUSH2, 0x01, 0x00, // 256
            (byte)Instruction.SHL,
            (byte)Instruction.STOP,
        ];

        SegmentRun run = Run(code, [], gasLimit: 100);

        Assert.That(run.StackBottomFirst, Is.EqualTo(new UInt256[] { 0 }), "shifting by 256 or more yields zero");
    }

    [Test]
    public void Compile_SarOnNegativeWithHugeShift_YieldsAllOnes()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH1, 1,
            (byte)Instruction.NOT,             // 0xFF...FE — negative as Int256
            (byte)Instruction.PUSH2, 0x01, 0x00, // shift 256
            (byte)Instruction.SAR,
            (byte)Instruction.STOP,
        ];

        SegmentRun run = Run(code, [], gasLimit: 100);

        Assert.That(run.StackBottomFirst, Is.EqualTo(new[] { UInt256.MaxValue }), "arithmetic shift of a negative value saturates to -1");
    }

    [TestCase(0, 0xFFul, TestName = "SignExtend_NegativeByte0_ExtendsToMinusOne")]
    [TestCase(40, 0xFFul, TestName = "SignExtend_IndexOutOfRange_LeavesValue")]
    public void Compile_SignExtend_MatchesEvmSemantics(int index, ulong value)
    {
        byte[] code =
        [
            (byte)Instruction.PUSH1, (byte)value,
            (byte)Instruction.PUSH1, (byte)index, // index on top
            (byte)Instruction.SIGNEXTEND,
            (byte)Instruction.STOP,
        ];

        SegmentRun run = Run(code, [], gasLimit: 100);

        UInt256 expected = index == 0 ? UInt256.MaxValue : (UInt256)value;
        Assert.That(run.StackBottomFirst, Is.EqualTo(new[] { expected }), "0xFF sign-extended from byte 0 is -1; out-of-range index changes nothing");
    }

    [Test]
    public void Compile_AddModAndSignedComparison_MatchEvmSemantics()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH1, 5,   // modulus (third)
            (byte)Instruction.PUSH1, 4,   // b (second)
            (byte)Instruction.PUSH1, 3,   // a (top)
            (byte)Instruction.ADDMOD,     // (3 + 4) % 5 = 2
            (byte)Instruction.PUSH1, 1,
            (byte)Instruction.NOT,        // -2 signed
            (byte)Instruction.SLT,        // top(-2) < second(2)? → 1
            (byte)Instruction.STOP,
        ];

        SegmentRun run = Run(code, [], gasLimit: 100);

        Assert.That(run.StackBottomFirst, Is.EqualTo(new UInt256[] { 1 }), "(3+4)%5 = 2, then signed -2 < 2 is true");
    }

    [Test]
    public void Compile_InterpreterOnlyBlock_IsRejected()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH1, 0,
            (byte)Instruction.SLOAD,
            (byte)Instruction.STOP,
        ];
        AnalyzedCode analyzed = BytecodeAnalyzer.Analyze(code, Spec);
        Assert.That(analyzed.Blocks[1].IsCompilable, Is.False, "precondition: the SLOAD block is interpreter-only");

        bool compiled = IlSegmentCompiler.TryCompile(code, analyzed.Blocks[1], out IlCompiledSegment? segment);

        Assert.That(compiled, Is.False, "interpreter-only blocks must not compile");
        Assert.That(segment, Is.Null, "no segment is produced for a rejected block");
    }

    private static SegmentRun Run(byte[] code, UInt256[] initialStackBottomFirst, long gasLimit)
    {
        AnalyzedCode analyzed = BytecodeAnalyzer.Analyze(code, Spec);
        Assert.That(analyzed.BlockCount, Is.GreaterThan(0), "precondition: the code analyzes into at least one block");
        bool compiled = IlSegmentCompiler.TryCompile(code, analyzed.Blocks[0], out IlCompiledSegment? segment);
        Assert.That(compiled, Is.True, "precondition: the first block must compile");

        byte[] stackBytes = new byte[(EvmStack.MaxStackSize + EvmStack.RegisterLength) * EvmStack.WordSize];
        EvmStack stack = new(0, NullTxTracer.Instance, ref MemoryMarshal.GetArrayDataReference(stackBytes), code);
        foreach (UInt256 value in initialStackBottomFirst)
        {
            EvmExceptionType pushed = stack.PushUInt256<OffFlag>(in value);
            Assert.That(pushed, Is.EqualTo(EvmExceptionType.None), "precondition: seeding the stack must succeed");
        }

        EthereumGasPolicy gas = EthereumGasPolicy.FromLong(gasLimit);
        int programCounter = segment!.EntryPc;
        EvmExceptionType result = segment.Invoke(ref stack, ref gas, ref programCounter);

        UInt256[] finalStack = new UInt256[stack.Head];
        for (int i = finalStack.Length - 1; i >= 0; i--)
        {
            Assert.That(stack.PopUInt256(out finalStack[i]), Is.True, "draining the final stack must succeed");
        }

        return new SegmentRun(result, finalStack, EthereumGasPolicy.GetRemainingGas(in gas), programCounter, segment);
    }

    private sealed record SegmentRun(
        EvmExceptionType Result,
        UInt256[] StackBottomFirst,
        long GasLeft,
        int ProgramCounter,
        IlCompiledSegment Segment);
}
