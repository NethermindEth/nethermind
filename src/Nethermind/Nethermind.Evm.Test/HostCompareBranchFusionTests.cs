// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nethermind.Core.Specs;
using NUnit.Framework;
using Harness = Nethermind.Evm.Test.HostMemoryFastPathTests.Harness;
using Outcome = Nethermind.Evm.Test.HostMemoryFastPathTests.Outcome;
using Setup = Nethermind.Evm.Test.HostMemoryFastPathTests.Setup;
using Table = Nethermind.Evm.Test.HostMemoryFastPathTests.Table;

namespace Nethermind.Evm.Test;

/// <summary>Runs comparisons through the host's untraced tables, which fuse each with the branch after it.</summary>
/// <remarks>
/// Each run is compared with the tables' plain halves, which run every opcode on its own, and with the traced table:
/// the fault, gas left, program counter, stack and opcode count must agree. The cases sit where the fused handler
/// chooses - the end of the code, the destination, the depth of the stack, the ISZERO between - and each case that
/// succeeds also runs with every smaller amount of gas, which puts a gas boundary inside every fused step.
/// </remarks>
[Parallelizable(ParallelScope.All)]
public class HostCompareBranchFusionTests
{
    private const byte STOP = (byte)Instruction.STOP;
    private const byte SUB = (byte)Instruction.SUB;
    private const byte LT = (byte)Instruction.LT;
    private const byte GT = (byte)Instruction.GT;
    private const byte SLT = (byte)Instruction.SLT;
    private const byte SGT = (byte)Instruction.SGT;
    private const byte EQ = (byte)Instruction.EQ;
    private const byte ISZERO = (byte)Instruction.ISZERO;
    private const byte POP = (byte)Instruction.POP;
    private const byte JUMP = (byte)Instruction.JUMP;
    private const byte JUMPI = (byte)Instruction.JUMPI;
    private const byte JUMPDEST = (byte)Instruction.JUMPDEST;
    private const byte PUSH1 = (byte)Instruction.PUSH1;
    private const byte PUSH2 = (byte)Instruction.PUSH2;
    private const byte PUSH4 = (byte)Instruction.PUSH4;
    private const byte PUSH32 = (byte)Instruction.PUSH32;
    private const byte DUP1 = (byte)Instruction.DUP1;
    private const byte DUP2 = (byte)Instruction.DUP2;
    private const byte SWAP1 = (byte)Instruction.SWAP1;

    private const ulong AmpleGas = 1_000_000;
    /// <summary>The most gas a random program gets, little enough to bound its loops.</summary>
    private const ulong ProgramGas = 30_000;
    private const int ProgramsPerFork = 2000;
    private const int FullStack = EvmStack.MaxStackSize - 1;

    private static readonly byte[] Comparisons = [ISZERO, EQ, LT, GT, SLT, SGT];

    private static readonly byte[] Zero = new byte[32];
    private static readonly byte[] One = Word(31, 1);
    private static readonly byte[] HighOne = Word(0, 1);
    private static readonly byte[] MinusOne = Enumerable.Repeat((byte)0xff, 32).ToArray();
    private static readonly byte[] MostNegative = Word(0, 0x80);
    private static readonly byte[] MostPositive = [0x7f, .. Enumerable.Repeat((byte)0xff, 31)];

    /// <summary>
    /// Top and second operands for each comparison, so that it holds and fails, and so that a high limb, a low limb or
    /// the sign decides it.
    /// </summary>
    private static (string Name, byte[] Top, byte[] Second)[] Operands(byte comparison) => comparison switch
    {
        ISZERO => [("0", Zero, Zero), ("1", One, Zero), ("2^248", HighOne, Zero)],
        EQ => [("2^248 2^248", HighOne, HighOne), ("1 0", One, Zero), ("2^248 0", HighOne, Zero), ("-1 -1", MinusOne, MinusOne)],
        LT or GT => [("1 2^248", One, HighOne), ("2^248 1", HighOne, One), ("1 1", One, One), ("-1 1", MinusOne, One)],
        _ => [("-1 1", MinusOne, One), ("1 -1", One, MinusOne), ("min max", MostNegative, MostPositive), ("max min", MostPositive, MostNegative), ("1 1", One, One)],
    };

    /// <summary>Whether <paramref name="comparison"/> holds for the operands, as the EVM defines it.</summary>
    private static bool Holds(byte comparison, byte[] top, byte[] second)
    {
        bool unsigned = comparison is not (SLT or SGT);
        BigInteger a = new(top, unsigned, isBigEndian: true);
        BigInteger b = new(second, unsigned, isBigEndian: true);
        return comparison switch
        {
            ISZERO => a.IsZero,
            EQ => a == b,
            LT or SLT => a < b,
            _ => a > b,
        };
    }

    private static IEnumerable<TestCaseData> Cases()
    {
        foreach (byte comparison in Comparisons)
        {
            string name = ((Instruction)comparison).ToString();
            foreach ((string values, byte[] top, byte[] second) in Operands(comparison))
            {
                byte[] operands = comparison == ISZERO ? [PUSH32, .. top] : [PUSH32, .. second, PUSH32, .. top];
                foreach (bool inverted in (bool[])[false, true])
                {
                    byte[] shape = [.. operands, comparison, .. (inverted ? (byte[])[ISZERO] : [])];
                    string label = $"{name}{(inverted ? " ISZERO" : "")} PUSH2 JUMPI on {values}";
                    bool taken = Holds(comparison, top, second) != inverted;
                    int push = shape.Length;
                    // Each outcome leaves a marker, so a wrong branch shows on the stack.
                    yield return Case($"{label} onto a JUMPDEST",
                        [.. shape, .. Push2(push + 7), JUMPI, PUSH1, 0xaa, STOP, JUMPDEST, PUSH1, 0xbb, STOP], fuses: true);
                    // A branch that ends the code runs off the end when not taken.
                    yield return Case($"{label} ending the code onto a JUMPDEST", [.. shape, .. Push2(push + 4), JUMPI, JUMPDEST], fuses: true);
                    // Not taken, the destination is never validated; taken, the comparison runs alone before the JUMPI faults.
                    bool? fusesUnlessTaken = taken ? null : true;
                    yield return Case($"{label} onto a non-JUMPDEST", [.. shape, .. Push2(push + 6), JUMPI, PUSH1, 0xaa, STOP], fusesUnlessTaken);
                    yield return Case($"{label} into PUSH data", [.. shape, .. Push2(push + 5), JUMPI, PUSH1, JUMPDEST, STOP], fusesUnlessTaken);
                    yield return Case($"{label} past the code", [.. shape, .. Push2(0xffff), JUMPI, JUMPDEST], fusesUnlessTaken);
                }
            }

            byte[] pair = comparison == ISZERO ? [PUSH1, 1] : [PUSH1, 1, PUSH1, 2];
            // The code ends inside the branch: its JUMPI or immediates would be the padding's STOP.
            yield return Case($"{name} ending the code", [.. pair, comparison], fuses: false);
            yield return Case($"{name} PUSH2 ending the code", [.. pair, comparison, PUSH2], fuses: false);
            yield return Case($"{name} PUSH2 with one immediate", [.. pair, comparison, PUSH2, 0], fuses: false);
            yield return Case($"{name} PUSH2 without a JUMPI", [.. pair, comparison, PUSH2, 0, 0], fuses: false);
            yield return Case($"{name} ISZERO ending the code", [.. pair, comparison, ISZERO], fuses: false);
            yield return Case($"{name} ISZERO PUSH2 without a JUMPI", [.. pair, comparison, ISZERO, PUSH2, 0, 0], fuses: false);

            // Shapes that do not fuse: the branch is not a PUSH2 JUMPI right after the comparison or one ISZERO.
            yield return Case($"{name} PUSH1 JUMPI", [PUSH1, 0, PUSH1, 0, comparison, PUSH1, 9, JUMPI, STOP, JUMPDEST, STOP], fuses: false);
            yield return Case($"{name} PUSH2 JUMP", [PUSH1, 0, PUSH1, 0, comparison, .. Push2(10), JUMP, STOP, JUMPDEST, STOP], fuses: false);
            yield return Case($"{name} DUP1 PUSH2 JUMPI", [PUSH1, 0, PUSH1, 0, comparison, DUP1, .. Push2(11), JUMPI, STOP, JUMPDEST, STOP], fuses: false);
            // The comparison runs alone, and the ISZERO after it fuses with the second ISZERO and the branch.
            yield return Case($"{name} ISZERO ISZERO PUSH2 JUMPI", [PUSH1, 0, PUSH1, 0, comparison, ISZERO, ISZERO, .. Push2(12), JUMPI, STOP, JUMPDEST, STOP], fuses: true);

            // Stacks short of an operand, and full ones: only ISZERO can leave a stack the branch's PUSH2 overflows.
            yield return Case($"{name} PUSH2 JUMPI on an empty stack", [comparison, .. Push2(5), JUMPI, JUMPDEST, STOP], fuses: null);
            if (comparison != ISZERO)
                yield return Case($"{name} PUSH2 JUMPI with one operand", [PUSH1, 1, comparison, .. Push2(7), JUMPI, JUMPDEST, STOP], fuses: null);
            foreach (int depth in (int[])[FullStack - 1, FullStack])
            {
                bool? fuses = comparison == ISZERO && depth == FullStack ? null : true;
                foreach (byte topByte in (byte[])[0, 1])
                {
                    yield return Case($"{name} PUSH2 JUMPI on {depth} words topped by {topByte}",
                        [comparison, .. Push2(5), JUMPI, JUMPDEST, STOP], fuses, Stack(depth, topByte));
                    yield return Case($"{name} ISZERO PUSH2 JUMPI on {depth} words topped by {topByte}",
                        [comparison, ISZERO, .. Push2(6), JUMPI, JUMPDEST, STOP], fuses, Stack(depth, topByte));
                }
            }
        }

        // Loops long enough for the cancelable table's poll, which only a taken branch reaches, to fall inside them.
        foreach ((string name, byte[] test) in ((string, byte[])[])[
            ("LT ISZERO", [PUSH1, 1, DUP2, LT, ISZERO]),
            ("GT", [PUSH1, 0, DUP2, GT]),
            ("SGT", [PUSH1, 0, DUP2, SGT]),
            ("SLT ISZERO", [PUSH1, 1, DUP2, SLT, ISZERO]),
            ("ISZERO ISZERO", [DUP1, ISZERO, ISZERO]),
            ("EQ ISZERO", [PUSH1, 0, DUP2, EQ, ISZERO])])
        {
            // Counts down: each iteration decrements the counter and branches back while it is not zero.
            yield return Case($"{name} PUSH2 JUMPI countdown loop",
                [.. Push2(150), JUMPDEST, PUSH1, 1, SWAP1, SUB, .. test, .. Push2(3), JUMPI, STOP], fuses: true);
        }

        // A selector dispatcher: the first comparison passes over, the second matches.
        yield return Case("DUP1 PUSH4 EQ PUSH2 JUMPI dispatcher",
            [PUSH4, 0xaa, 0xbb, 0xcc, 0xdd,
                DUP1, PUSH4, 0x11, 0x22, 0x33, 0x44, EQ, .. Push2(28), JUMPI,
                DUP1, PUSH4, 0xaa, 0xbb, 0xcc, 0xdd, EQ, .. Push2(30), JUMPI,
                STOP, JUMPDEST, STOP, JUMPDEST, POP, STOP], fuses: true);
    }

    /// <summary>
    /// Every case matches the plain handlers on ample gas and on every amount up to what it uses; with ample gas, a case
    /// that fuses counts none of its opcodes on the machine, where the cancelable table's PUSH2 running the JUMPI would
    /// count it, and never falls back on a plain handler, which would leave the branch to the next opcode's handler to
    /// fuse.
    /// </summary>
    /// <param name="fuses">Whether the branches fuse; <see langword="null"/> where the case faults.</param>
    [TestCaseSource(nameof(Cases))]
    public void Fused_tables_match_the_plain_handlers_on_every_amount_of_gas(byte[] code, bool? fuses, byte[] stack)
    {
        Harness harness = new();
        Outcome plain = harness.Run(code, AmpleGas, Table.PlainNoTrace, Setup.Fresh, stack: stack);
        int plainMachineOpCodes = harness.MachineOpCodeCount;
        ulong gasUsed = HostMemoryFastPathTests.IsFault(plain.Exception) ? 0 : AmpleGas - plain.GasLeft;
        List<string> mismatches = [];
        for (ulong gas = 0; gas <= gasUsed && mismatches.Count < 5; gas++)
            harness.Compare(code, gas, Setup.Fresh, [], mismatches, stack);
        harness.Compare(code, AmpleGas, Setup.Fresh, [], mismatches, stack);

        // The untraced table's PUSH2 counts the JUMP or JUMPI it runs in the dispatch counter, not on the machine, so only
        // the cancelable table's plain half still tells a fused branch from a PUSH2 that runs the JUMPI.
        harness.Run(code, AmpleGas, Table.PlainNoTraceCancelable, Setup.Fresh, stack: stack);
        int plainCancelableMachineOpCodes = harness.MachineOpCodeCount;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mismatches, Is.Empty);
            Assert.That(HostMemoryFastPathTests.IsFault(plain.Exception), Is.EqualTo(fuses is null), "the case faults");
            (Table Table, int PlainOpCodes)[] tables = [(Table.NoTrace, plainMachineOpCodes), (Table.NoTraceCancelable, plainCancelableMachineOpCodes)];
            foreach ((Table table, int plainOpCodes) in tables)
            {
                harness.Run(code, AmpleGas, table, Setup.Fresh, stack: stack, countFallbacks: true);
                if (fuses == true)
                    Assert.That((harness.MachineOpCodeCount, harness.Fallbacks), Is.EqualTo((0, 0)), $"{table} fuses");
                else
                    Assert.That(harness.MachineOpCodeCount, Is.EqualTo(plainOpCodes), $"{table} does not fuse");
            }

            if (fuses == true)
                Assert.That(plainCancelableMachineOpCodes, Is.Positive, "a PUSH2 running the JUMPI counts it on the machine");
        }
    }

    /// <summary>
    /// Both untraced tables dispatch every comparison to its fused handler, not to the plain one. The untraced table's
    /// PUSH2 counts a JUMPI it runs in the dispatch counter, as a fused branch does, so the opcode counts above cannot
    /// tell the two apart there.
    /// </summary>
    [TestCase(Table.NoTrace)]
    [TestCase(Table.NoTraceCancelable)]
    public void Untraced_tables_install_the_fused_comparisons(Table table)
    {
        Harness harness = new();
        using (Assert.EnterMultipleScope())
        {
            foreach (Instruction opcode in (Instruction[])[Instruction.ISZERO, Instruction.EQ, Instruction.LT, Instruction.GT, Instruction.SLT, Instruction.SGT])
            {
                (nint entry, nint plain) = harness.Handlers(table, opcode);
                Assert.That(entry, Is.Not.EqualTo(plain), $"{table} {opcode}");
            }
        }
    }

    /// <summary>
    /// Random programs on every fork, built from the shapes compilers branch with, over destinations that are, are not, or
    /// only look like a JUMPDEST: a fork that changes what the plain handlers charge must change the fused ones too, or
    /// fail here.
    /// </summary>
    [TestCaseSource(typeof(HostMemoryFastPathTests), nameof(HostMemoryFastPathTests.Forks))]
    public void Random_programs_match_the_plain_handlers(IReleaseSpec fork, int seed)
    {
        Harness harness = new(fork);
        List<string> mismatches = [];
        for (int i = 0; i < ProgramsPerFork && mismatches.Count < 5; i++)
        {
            Random random = new(seed * 1_000_003 + i);
            byte[] code = Generate(random);
            byte[] stack = RandomStack(random);
            ulong gas = random.Next(4) switch
            {
                0 => (ulong)random.Next(0, 60),
                1 => (ulong)random.Next(0, 400),
                2 => (ulong)random.Next(0, 3000),
                _ => ProgramGas,
            };

            harness.Compare(code, gas, Setup.Fresh, [], mismatches, stack);
            // Gas boundaries: exactly what a plain run uses, then one and two short of it.
            Outcome plain = harness.Run(code, ProgramGas, Table.PlainNoTrace, Setup.Fresh, stack: stack);
            if (HostMemoryFastPathTests.IsFault(plain.Exception)) continue;
            ulong used = ProgramGas - plain.GasLeft;
            for (ulong shortBy = 0; shortBy <= Math.Min(used, 2UL); shortBy++)
                harness.Compare(code, used - shortBy, Setup.Fresh, [], mismatches, stack);
        }

        Assert.That(mismatches, Is.Empty);
    }

    /// <summary>A program of operand pushes, comparisons and branches, whose destinations are patched in once the code is laid out.</summary>
    private static byte[] Generate(Random random)
    {
        List<byte> code = [];
        List<int> jumpDestinations = [];
        List<int> destinations = [];
        List<int> pushData = [];
        int snippets = random.Next(4, 40);
        for (int s = 0; s < snippets; s++)
        {
            switch (random.Next(100))
            {
                case < 20: PushOperand(code, random); break;
                case < 26: code.Add(random.Next(2) == 0 ? DUP1 : DUP2); break;
                case < 29: code.Add(SWAP1); break;
                case < 31: code.Add(POP); break;
                case < 50: code.Add(Comparisons[random.Next(Comparisons.Length)]); break;
                case < 55: code.Add(ISZERO); break;
                case < 75:
                    code.Add(Comparisons[random.Next(Comparisons.Length)]);
                    if (random.Next(3) == 0) code.Add(ISZERO);
                    destinations.Add(code.Count + 1);
                    code.AddRange([PUSH2, 0, 0, JUMPI]);
                    break;
                case < 80:
                    destinations.Add(code.Count + 1);
                    code.AddRange([PUSH2, 0, 0, random.Next(2) == 0 ? JUMPI : JUMP]);
                    break;
                case < 83: code.AddRange([PUSH1, (byte)random.Next(0, code.Count + 8), JUMPI]); break;
                case < 95:
                    jumpDestinations.Add(code.Count);
                    code.Add(JUMPDEST);
                    break;
                case < 97:
                    // A JUMPDEST byte that is PUSH data, which no jump may land on.
                    pushData.Add(code.Count + 1);
                    code.AddRange([PUSH1, JUMPDEST, POP]);
                    break;
                default: code.Add(STOP); break;
            }
        }

        if (random.Next(4) == 0) code.Add(Comparisons[random.Next(Comparisons.Length)]);
        foreach (int at in destinations)
        {
            int destination = random.Next(10) switch
            {
                < 7 when jumpDestinations.Count > 0 => jumpDestinations[random.Next(jumpDestinations.Count)],
                7 when pushData.Count > 0 => pushData[random.Next(pushData.Count)],
                8 => code.Count + random.Next(0, 3),
                _ => random.Next(0, code.Count),
            };
            code[at] = (byte)(destination >> 8);
            code[at + 1] = (byte)destination;
        }

        return code.ToArray();
    }

    private static void PushOperand(List<byte> code, Random random)
    {
        switch (random.Next(6))
        {
            case 0: code.AddRange([PUSH1, 0]); break;
            case 1: code.AddRange([PUSH1, (byte)random.Next(0, 4)]); break;
            case 2: code.AddRange([PUSH32, .. MinusOne]); break;
            case 3: code.AddRange([PUSH32, .. (random.Next(2) == 0 ? MostNegative : MostPositive)]); break;
            case 4: code.AddRange([PUSH32, .. HighOne]); break;
            default:
                code.Add(PUSH32);
                for (int b = 0; b < 32; b++) code.Add((byte)(random.Next(3) == 0 ? random.Next(256) : 0));
                break;
        }
    }

    /// <summary>A few words for a program to start on, or a stack one short of full or full, topped by zero or one.</summary>
    private static byte[] RandomStack(Random random) => random.Next(20) switch
    {
        0 => Stack(FullStack - 1, (byte)random.Next(2)),
        1 => Stack(FullStack, (byte)random.Next(2)),
        _ => Stack(random.Next(0, 4), (byte)random.Next(2)),
    };

    /// <summary>A stack of <paramref name="depth"/> zero words, the top one holding <paramref name="top"/> in its low byte, in limb layout.</summary>
    private static byte[] Stack(int depth, byte top)
    {
        byte[] stack = new byte[depth * EvmStack.WordSize];
        if (depth > 0) stack[(depth - 1) * EvmStack.WordSize] = top;
        return stack;
    }

    private static TestCaseData Case(string name, byte[] code, bool? fuses, byte[]? stack = null) =>
        new TestCaseData(code, fuses, stack ?? []).SetName($"{{m}}({name})");

    private static byte[] Push2(int value) => [PUSH2, (byte)(value >> 8), (byte)value];

    /// <summary>A big-endian word whose only non-zero byte is <paramref name="value"/> at <paramref name="index"/>.</summary>
    private static byte[] Word(int index, byte value)
    {
        byte[] word = new byte[32];
        word[index] = value;
        return word;
    }
}
