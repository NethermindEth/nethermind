// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Differential tests for the untraced handlers that run an opcode sequence as one dispatch: comparison or ISZERO
/// followed by <c>PUSH2</c> <c>JUMPI</c> (with an optional ISZERO between), <c>DUP1</c> <c>PUSH4</c> <c>EQ</c>
/// <c>PUSH2</c> <c>JUMPI</c>, and runs of POP.
/// </summary>
/// <remarks>
/// Each program runs twice: once untraced, where the fused handlers are installed, and once with an instruction
/// tracer, whose table has only the plain handlers. Status, gas, output, error and opcode count must match.
/// </remarks>
[Parallelizable(ParallelScope.Self)]
public class OpcodeSequenceFusionTests : VirtualMachineTestsBase
{
    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.CancunBlockTimestamp;

    private const ulong IntrinsicGas = 21000;
    private bool _traced;

    protected override TestAllTracerWithOutput CreateTracer() => _traced ? new TestAllTracerWithOutput() : new NoInstructionTracer();

    private sealed class NoInstructionTracer : TestAllTracerWithOutput
    {
        public override bool IsTracingInstructions => false;
    }

    /// <summary>A cancelable tracer that counts its polls.</summary>
    /// <remarks>Untraced, it runs the cancelable tables with the fused handlers; traced, the ones with only the plain handlers.</remarks>
    private sealed class PollCountingTracer(bool traceInstructions, int cancelAtPoll = int.MaxValue) : TestAllTracerWithOutput, ITxTracer
    {
        public int PollCount { get; private set; }

        public override bool IsTracingInstructions => traceInstructions;

        bool ITxTracer.IsCancelable => true;

        bool ITxTracer.IsCancelled => ++PollCount >= cancelAtPoll;
    }

    private readonly record struct Outcome(byte StatusCode, ulong GasSpent, string ReturnValue, string? Error, int OpCodeCount);

    private Outcome Run(byte[] code, ulong gasLimit, bool traced)
    {
        _traced = traced;
        TestAllTracerWithOutput result = Execute(Activation, gasLimit, code);
        return new Outcome(result.StatusCode, result.GasSpent, (result.ReturnValue ?? []).ToHexString(), result.Error, Machine.OpCodeCount);
    }

    private Outcome AssertSameAsPlain(byte[] code, ulong gasLimit = 100_000)
    {
        Outcome plain = Run(code, gasLimit, traced: true);
        Outcome fused = Run(code, gasLimit, traced: false);
        Assert.That(fused, Is.EqualTo(plain), $"code {code.ToHexString()} gas {gasLimit}");
        return fused;
    }

    /// <summary>Runs <paramref name="code"/> under every gas limit from none left for execution to enough for all of it.</summary>
    private void AssertSameAsPlainAtEveryGasLimit(byte[] code)
    {
        Outcome full = AssertSameAsPlain(code);
        Assert.That(full.StatusCode, Is.EqualTo(1), "the full run must succeed for the sweep to cover every opcode");
        for (ulong gasLimit = IntrinsicGas; gasLimit <= full.GasSpent; gasLimit++)
            AssertSameAsPlain(code, gasLimit);
    }

    private static readonly byte[] StoreTopAndReturn = [0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3];

    private static byte[] Code(params byte[][] parts)
    {
        List<byte> code = [];
        foreach (byte[] part in parts)
            code.AddRange(part);
        return [.. code];
    }

    private static byte[] Ops(params Instruction[] instructions) => Array.ConvertAll(instructions, static i => (byte)i);

    private static byte[] Push1(byte value) => [(byte)Instruction.PUSH1, value];

    private static byte[] Push2(int value) => [(byte)Instruction.PUSH2, (byte)(value >> 8), (byte)value];

    private static byte[] Push4(uint value) => [(byte)Instruction.PUSH4, (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] Push32(byte fill) => [(byte)Instruction.PUSH32, .. Repeat(fill, 32)];

    private static byte[] Repeat(byte value, int count)
    {
        byte[] bytes = new byte[count];
        bytes.AsSpan().Fill(value);
        return bytes;
    }

    public static IEnumerable<TestCaseData> ConditionCases()
    {
        Instruction[] conditions = [Instruction.EQ, Instruction.LT, Instruction.GT, Instruction.SLT, Instruction.SGT, Instruction.ISZERO];
        byte[][] operands = [[1, 1], [1, 2], [2, 1], [0, 0], [0xff, 1]];
        foreach (Instruction condition in conditions)
            foreach (byte[] pair in operands)
                foreach (bool inverted in new[] { false, true })
                    yield return new TestCaseData(condition, pair[0], pair[1], inverted)
                        .SetName($"{condition}({pair[0]},{pair[1]}){(inverted ? " ISZERO" : "")} PUSH2 JUMPI");
    }

    /// <summary>A comparison, optionally inverted, then <c>PUSH2</c> <c>JUMPI</c>, at every gas limit.</summary>
    /// <remarks>A 0xff operand becomes a word of all ones, so the signed comparisons see a negative value.</remarks>
    [TestCaseSource(nameof(ConditionCases))]
    public void Condition_branch_matches_plain_execution(Instruction condition, byte a, byte b, bool inverted)
    {
        byte[] operandA = a == 0xff ? Push32(0xff) : Push1(a);
        byte[] operandB = b == 0xff ? Push32(0xff) : Push1(b);
        //   operands, condition, [ISZERO], PUSH2 taken, JUMPI, PUSH1 0x11, store and return, JUMPDEST, PUSH1 0x22, store and return
        byte[] head = Code(operandB, operandA, Ops(condition), inverted ? Ops(Instruction.ISZERO) : []);
        int target = head.Length + 3 + 1 + 2 + StoreTopAndReturn.Length;
        byte[] code = Code(head, Push2(target), Ops(Instruction.JUMPI), Push1(0x11), StoreTopAndReturn,
            Ops(Instruction.JUMPDEST), Push1(0x22), StoreTopAndReturn);

        AssertSameAsPlainAtEveryGasLimit(code);
    }

    [TestCase(0x12345678u, 0x12345678u)]
    [TestCase(0x12345678u, 0x12345679u)]
    [TestCase(0x00000000u, 0x00000000u)]
    public void Selector_branch_matches_plain_execution(uint selector, uint tested)
    {
        //   PUSH4 selector, DUP1, PUSH4 tested, EQ, PUSH2 taken, JUMPI, PUSH1 0x11, store and return, JUMPDEST, PUSH1 0x22, store and return
        byte[] head = Code(Push4(selector), Ops(Instruction.DUP1), Push4(tested), Ops(Instruction.EQ));
        int target = head.Length + 3 + 1 + 2 + StoreTopAndReturn.Length;
        byte[] code = Code(head, Push2(target), Ops(Instruction.JUMPI), Push1(0x11), StoreTopAndReturn,
            Ops(Instruction.JUMPDEST), Push1(0x22), StoreTopAndReturn);

        AssertSameAsPlainAtEveryGasLimit(code);
    }

    [Test]
    public void Selector_branch_does_not_match_a_word_wider_than_the_selector()
    {
        // The selector's bytes sit in the low limb, but a higher limb is nonzero.
        byte[] word = new byte[32];
        word[0] = 1;
        word[28] = 0x12;
        word[29] = 0x34;
        word[30] = 0x56;
        word[31] = 0x78;
        byte[] head = Code([(byte)Instruction.PUSH32, .. word], Ops(Instruction.DUP1), Push4(0x12345678), Ops(Instruction.EQ));
        int target = head.Length + 3 + 1 + 2 + StoreTopAndReturn.Length;
        byte[] code = Code(head, Push2(target), Ops(Instruction.JUMPI), Push1(0x11), StoreTopAndReturn,
            Ops(Instruction.JUMPDEST), Push1(0x22), StoreTopAndReturn);

        Outcome outcome = AssertSameAsPlain(code);
        Assert.That(outcome.ReturnValue, Does.EndWith("11"));
    }

    /// <summary>
    /// Endless loops whose back edge is a taken fused branch, each written with <c>PUSH2</c>, which the fused handlers
    /// take, and with <c>PUSH3</c>, which they do not. Both forms keep the stack height and cost the same gas per opcode.
    /// </summary>
    private static readonly (string Name, string Fused, string Unfused)[] TakenBranchLoops =
    [
        ("GT PUSH2 JUMPI", "5b600160021161000057", "5b60016002116200000057"),
        ("ISZERO ISZERO PUSH2 JUMPI", "5b6001151561000057", "5b600115156200000057"),
        ("DUP1 PUSH4 EQ PUSH2 JUMPI", "63123456785b8063123456781461000557", "63123456785b806312345678146200000557"),
    ];

    private static IEnumerable<TestCaseData> TakenBranchLoopCases()
    {
        foreach ((string name, string fused, string unfused) in TakenBranchLoops)
            yield return new TestCaseData(Bytes.FromHexString(fused), Bytes.FromHexString(unfused)).SetArgDisplayNames(name);
    }

    private static IEnumerable<TestCaseData> FusedLoopCases()
    {
        foreach ((string name, string fused, _) in TakenBranchLoops)
            yield return new TestCaseData(Bytes.FromHexString(fused)).SetArgDisplayNames(name);
    }

    /// <summary>
    /// On the cancelable tables, which <c>eth_call</c> runs, a taken fused branch polls cancellation where the plain
    /// <c>JUMPI</c> does, and execution resumes at the jump destination after the poll.
    /// </summary>
    [TestCaseSource(nameof(TakenBranchLoopCases))]
    public void Taken_branch_on_a_cancelable_table_polls_as_plain_execution(byte[] fusedCode, byte[] unfusedCode)
    {
        PollCountingTracer plain = Execute(new PollCountingTracer(traceInstructions: true), fusedCode);
        int plainOpcodes = Machine.OpCodeCount;
        PollCountingTracer unfused = Execute(new PollCountingTracer(traceInstructions: false), unfusedCode);
        int unfusedOpcodes = Machine.OpCodeCount;
        PollCountingTracer fused = Execute(new PollCountingTracer(traceInstructions: false), fusedCode);
        int fusedOpcodes = Machine.OpCodeCount;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fused.Error, Is.EqualTo(nameof(EvmExceptionType.OutOfGas)), "precondition: the loop only ends by running out of gas");
            Assert.That(fused.Error, Is.EqualTo(plain.Error));
            Assert.That(fused.GasSpent, Is.EqualTo(plain.GasSpent));
            Assert.That(fusedOpcodes, Is.EqualTo(plainOpcodes));
            Assert.That(fusedOpcodes, Is.EqualTo(unfusedOpcodes));
            Assert.That(fused.PollCount, Is.GreaterThan(2), "the loop passes the poll interval several times");
            Assert.That(fused.PollCount, Is.EqualTo(unfused.PollCount), "taken fused branches poll where unfused taken branches do");
        }
    }

    [TestCaseSource(nameof(FusedLoopCases))]
    public void Taken_branch_on_a_cancelable_table_stops_a_cancelled_loop(byte[] fusedCode)
    {
        PollCountingTracer tracer = new(traceInstructions: false, cancelAtPoll: 2);

        Assert.Throws<OperationCanceledException>(() => Execute(tracer, fusedCode));
        Assert.That(tracer.PollCount, Is.EqualTo(2), "the first poll is at frame entry and the second at a taken fused branch");
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(9)]
    public void Pop_run_matches_plain_execution(int pops)
    {
        byte[] pushes = new byte[2 * (pops + 1)];
        for (int i = 0; i < pops + 1; i++)
        {
            pushes[2 * i] = (byte)Instruction.PUSH1;
            pushes[2 * i + 1] = (byte)i;
        }

        byte[] code = Code(pushes, Repeat((byte)Instruction.POP, pops), StoreTopAndReturn);

        AssertSameAsPlainAtEveryGasLimit(code);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(3)]
    public void Pop_run_longer_than_the_stack_underflows_at_the_same_opcode(int depth)
    {
        byte[] pushes = new byte[2 * depth];
        for (int i = 0; i < depth; i++)
        {
            pushes[2 * i] = (byte)Instruction.PUSH1;
            pushes[2 * i + 1] = 1;
        }

        Outcome outcome = AssertSameAsPlain(Code(pushes, Repeat((byte)Instruction.POP, 4), Ops(Instruction.STOP)));
        Assert.That(outcome.Error, Is.EqualTo(EvmExceptionType.StackUnderflow.ToString()));
    }

    [TestCase(Instruction.EQ)]
    [TestCase(Instruction.LT)]
    [TestCase(Instruction.ISZERO)]
    public void Condition_branch_on_a_short_stack_underflows_at_the_same_opcode(Instruction condition)
    {
        // One word short of the condition's inputs, then the branch shape.
        byte[] operands = condition == Instruction.ISZERO ? [] : Push1(1);
        byte[] code = Code(operands, Ops(condition), Push2(0), Ops(Instruction.JUMPI), Ops(Instruction.STOP));

        Outcome outcome = AssertSameAsPlain(code);
        Assert.That(outcome.Error, Is.EqualTo(EvmExceptionType.StackUnderflow.ToString()));
    }

    [Test]
    public void Selector_branch_on_an_empty_stack_underflows_at_the_same_opcode()
    {
        byte[] code = Code(Ops(Instruction.DUP1), Push4(1), Ops(Instruction.EQ), Push2(0), Ops(Instruction.JUMPI), Ops(Instruction.STOP));

        Outcome outcome = AssertSameAsPlain(code);
        Assert.That(outcome.Error, Is.EqualTo(EvmExceptionType.StackUnderflow.ToString()));
    }

    /// <summary>A sequence that starts on a stack at or near the limit overflows at the same opcode as plain execution.</summary>
    [TestCase(Instruction.ISZERO, 1024)]
    [TestCase(Instruction.ISZERO, 1023)]
    [TestCase(Instruction.DUP1, 1024)]
    [TestCase(Instruction.DUP1, 1023)]
    [TestCase(Instruction.DUP1, 1022)]
    public void Sequence_near_the_stack_limit_matches_plain_execution(Instruction first, int depth)
    {
        byte[] fill = Repeat((byte)Instruction.PUSH0, depth);
        byte[] sequence = first == Instruction.ISZERO
            ? Code(Ops(Instruction.ISZERO), Push2(0), Ops(Instruction.JUMPI))
            : Code(Ops(Instruction.DUP1), Push4(1), Ops(Instruction.EQ), Push2(0), Ops(Instruction.JUMPI));
        byte[] code = Code(fill, sequence, Ops(Instruction.STOP));

        AssertSameAsPlain(code, 1_000_000);
    }

    [TestCase(Instruction.EQ)]
    [TestCase(Instruction.ISZERO)]
    [TestCase(Instruction.DUP1)]
    public void Taken_branch_to_a_byte_that_is_not_a_jump_destination_fails_at_the_same_opcode(Instruction first)
    {
        // The destination is the immediate of a PUSH1 whose value is the JUMPDEST byte.
        byte[] head = first switch
        {
            Instruction.EQ => Code(Push1(1), Push1(1), Ops(Instruction.EQ)),
            Instruction.ISZERO => Code(Push1(0), Ops(Instruction.ISZERO)),
            _ => Code(Push4(7), Ops(Instruction.DUP1), Push4(7), Ops(Instruction.EQ)),
        };
        int target = head.Length + 3 + 1 + 1;
        byte[] code = Code(head, Push2(target), Ops(Instruction.JUMPI), Push1((byte)Instruction.JUMPDEST), Ops(Instruction.STOP));

        Outcome outcome = AssertSameAsPlain(code);
        Assert.That(outcome.Error, Is.EqualTo(EvmExceptionType.InvalidJumpDestination.ToString()));
    }

    /// <summary>A jump that lands inside a sequence's shape runs the opcodes after the landing point one at a time.</summary>
    [Test]
    public void Jump_into_the_middle_of_a_sequence_matches_plain_execution()
    {
        //   [0] PUSH1 1, [2] PUSH1 0, [4] PUSH2 10, [7] JUMP, [8] PUSH1 0 (skipped),
        //   [10] JUMPDEST, [11] ISZERO, [12] PUSH2 19, [15] JUMPI (taken),
        //   [16] PUSH1 0 (skipped), [18] ISZERO (skipped), [19] JUMPDEST, [20] PUSH2 27, [23] JUMPI (taken),
        //   [24] PUSH1 0x11, [26] STOP, [27] JUMPDEST, [28] PUSH1 0x22, store and return.
        // The jump lands on the JUMPDEST inside ISZERO JUMPDEST PUSH2 JUMPI, whose JUMPDEST breaks the shape anyway.
        byte[] code = Code(
            Push1(1), Push1(0), Push2(10), Ops(Instruction.JUMP), Push1(0),
            Ops(Instruction.JUMPDEST, Instruction.ISZERO), Push2(19), Ops(Instruction.JUMPI),
            Push1(0), Ops(Instruction.ISZERO),
            Ops(Instruction.JUMPDEST), Push2(27), Ops(Instruction.JUMPI),
            Push1(0x11), Ops(Instruction.STOP),
            Ops(Instruction.JUMPDEST), Push1(0x22), StoreTopAndReturn);

        Outcome outcome = AssertSameAsPlain(code);
        Assert.That(outcome.ReturnValue, Does.EndWith("22"));
        AssertSameAsPlainAtEveryGasLimit(code);
    }

    /// <summary>Random forward-branching programs built from the sequence shapes and the opcodes around them.</summary>
    [Test]
    public void Random_programs_match_plain_execution([Range(0, 399)] int seed)
    {
        Random random = new(seed);
        byte[] code = RandomProgram(random);
        AssertSameAsPlain(code);
        // A tight limit that runs out somewhere inside the program.
        Outcome full = Run(code, 100_000, traced: true);
        if (full.GasSpent > IntrinsicGas)
            AssertSameAsPlain(code, IntrinsicGas + (ulong)random.NextInt64(0, (long)(full.GasSpent - IntrinsicGas)));
    }

    private static byte[] RandomProgram(Random random)
    {
        const int Blocks = 8;
        List<byte> code = [];
        List<(int Offset, int Block)> patches = [];
        int[] blockStarts = new int[Blocks + 1];
        uint[] selectors = [0x12345678, 0xa9059cbb, 0];
        Instruction[] conditions = [Instruction.EQ, Instruction.LT, Instruction.GT, Instruction.SLT, Instruction.SGT, Instruction.ISZERO];

        for (int block = 0; block < Blocks; block++)
        {
            blockStarts[block] = code.Count;
            if (block > 0)
                code.Add((byte)Instruction.JUMPDEST);

            int length = random.Next(2, 10);
            for (int i = 0; i < length; i++)
            {
                switch (random.Next(9))
                {
                    case 0:
                        code.Add((byte)Instruction.PUSH1);
                        code.Add((byte)random.Next(0, 3));
                        break;
                    case 1:
                        code.AddRange(Push4(selectors[random.Next(selectors.Length)]));
                        break;
                    case 2:
                        code.AddRange(random.Next(2) == 0 ? Push32(0xff) : Push1(0xff));
                        break;
                    case 3:
                        code.Add((byte)Instruction.DUP1);
                        break;
                    case 4:
                        code.Add((byte)conditions[random.Next(conditions.Length)]);
                        break;
                    case 5:
                        for (int pops = random.Next(1, 6); pops > 0; pops--)
                            code.Add((byte)Instruction.POP);
                        break;
                    case 6:
                        // A branch: comparison, optional ISZERO, PUSH2 to a later block or a bad target, JUMPI.
                        code.Add((byte)conditions[random.Next(conditions.Length)]);
                        if (random.Next(2) == 0)
                            code.Add((byte)Instruction.ISZERO);
                        AddBranch(random, code, patches, block, Blocks);
                        break;
                    case 7:
                        // A selector test.
                        code.Add((byte)Instruction.DUP1);
                        code.AddRange(Push4(selectors[random.Next(selectors.Length)]));
                        code.Add((byte)Instruction.EQ);
                        AddBranch(random, code, patches, block, Blocks);
                        break;
                    default:
                        code.Add((byte)Instruction.PUSH0);
                        break;
                }
            }
        }

        blockStarts[Blocks] = code.Count;
        code.Add((byte)Instruction.JUMPDEST);
        code.AddRange(StoreTopAndReturn);

        foreach ((int offset, int block) in patches)
        {
            // Block -1 names a byte that is not a jump destination.
            int target = block < 0 ? blockStarts[Blocks] + 1 : blockStarts[block];
            code[offset] = (byte)(target >> 8);
            code[offset + 1] = (byte)target;
        }

        return [.. code];
    }

    private static void AddBranch(Random random, List<byte> code, List<(int Offset, int Block)> patches, int block, int blocks)
    {
        code.Add((byte)Instruction.PUSH2);
        patches.Add((code.Count, random.Next(8) == 0 ? -1 : random.Next(block + 1, blocks + 1)));
        code.Add(0);
        code.Add(0);
        code.Add((byte)Instruction.JUMPI);
    }
}
