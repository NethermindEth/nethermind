// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited

using System;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core.Extensions;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using NUnit.Framework;

namespace Nethermind.IlEvm.Test;

/// <summary>
/// Random programs of the shapes compiled Solidity takes, with the vector forms on: words with every limb in use, masks,
/// powers of two and small negative constants as operands, comparisons, shifts, the stack shuffles between them, words
/// passed through memory and storage, and branches that end segments.
/// </summary>
public partial class IlEvmDifferentialTests
{
    private static readonly Instruction[] PatternBinary =
    [
        Instruction.ADD, Instruction.SUB, Instruction.MUL, Instruction.DIV, Instruction.SDIV, Instruction.MOD, Instruction.SMOD,
        Instruction.EXP, Instruction.SIGNEXTEND, Instruction.LT, Instruction.GT, Instruction.SLT, Instruction.SGT, Instruction.EQ,
        Instruction.AND, Instruction.OR, Instruction.XOR, Instruction.BYTE, Instruction.SHL, Instruction.SHR, Instruction.SAR,
        Instruction.ADD, Instruction.SUB, Instruction.AND, Instruction.OR, Instruction.LT, Instruction.GT, Instruction.EQ,
        Instruction.SHL, Instruction.SHR, Instruction.MUL, Instruction.DIV,
    ];

    /// <summary>A word compiled code treats specially, or one with every limb in use.</summary>
    internal static BigInteger PatternWord(Random random)
    {
        BigInteger two256 = BigInteger.One << 256;
        int k = random.Next(257);
        switch (random.Next(12))
        {
            case 0: return random.Next(0, 300);
            case 1: return k == 256 ? 0 : BigInteger.One << k;
            case 2: return (BigInteger.One << k) - 1;
            case 3: return (two256 - 1) ^ ((BigInteger.One << k) - 1);
            case 4: return two256 - random.Next(1, 300);
            case 5:
            {
                // Limbs from the boundary values.
                BigInteger w = 0;
                for (int limb = 0; limb < 4; limb++)
                {
                    ulong v = random.Next(6) switch
                    {
                        0 => 0,
                        1 => ulong.MaxValue,
                        2 => 1UL << 63,
                        3 => (ulong)random.Next(1, 4),
                        4 => ulong.MaxValue - (ulong)random.Next(0, 4),
                        _ => (ulong)random.NextInt64(),
                    };
                    w |= (BigInteger)v << (64 * limb);
                }
                return w;
            }
            case 6: return (BigInteger.One << (64 * random.Next(1, 4))) + random.Next(-3, 4);
            default:
            {
                byte[] bytes = new byte[33];
                random.NextBytes(bytes);
                bytes[32] = 0;
                int width = random.Next(1, 33);
                for (int b = width; b < 32; b++) bytes[b] = 0;
                return new BigInteger(bytes);
            }
        }
    }

    private static void PushWord(List<byte> code, BigInteger value)
    {
        value = ((value % (BigInteger.One << 256)) + (BigInteger.One << 256)) % (BigInteger.One << 256);
        if (value.IsZero)
        {
            code.Add((byte)Instruction.PUSH0);
            return;
        }

        byte[] big = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        code.Add((byte)(Instruction.PUSH1 + (byte)(big.Length - 1)));
        code.AddRange(big);
    }

    internal static byte[] PatternInput(Random random)
    {
        byte[] input = new byte[8 * 32 + random.Next(0, 31)];
        for (int i = 0; i < 8; i++)
        {
            byte[] w = ((PatternWord(random) % (BigInteger.One << 256) + (BigInteger.One << 256)) % (BigInteger.One << 256)).ToByteArray(isUnsigned: true, isBigEndian: true);
            Array.Copy(w, 0, input, i * 32 + 32 - w.Length, w.Length);
        }

        for (int i = 8 * 32; i < input.Length; i++) input[i] = (byte)random.Next(256);
        return input;
    }

    private static bool ExtendedOps => Environment.GetEnvironmentVariable("ILEVM_PATTERN_BASIC") != "1";

    private static bool NoBranches => Environment.GetEnvironmentVariable("ILEVM_PATTERN_NOBRANCH") == "1";

    /// <summary>One program; <paramref name="returned"/> is how many words it returns.</summary>
    internal static byte[] PatternProgram(Random random, out int returned)
    {
        List<byte> code = [];
        int live = 0;
        int memoryWords = 0;

        void Word()
        {
            switch (random.Next(10))
            {
                case 0 or 1 or 2:
                    PushWord(code, random.Next(9) * 32);
                    code.Add((byte)Instruction.CALLDATALOAD);
                    break;
                case 3:
                    PushWord(code, random.Next(0, 300));
                    code.Add((byte)Instruction.CALLDATALOAD);
                    break;
                case 4:
                    code.Add(random.Next(3) switch { 0 => (byte)Instruction.GAS, 1 => (byte)Instruction.CALLVALUE, _ => (byte)Instruction.CALLDATASIZE });
                    break;
                case 5:
                    PushWord(code, random.Next(0, 6));
                    code.Add((byte)Instruction.SLOAD);
                    break;
                case 6 when memoryWords > 0:
                    PushWord(code, random.Next(memoryWords) * 32 + 0x400);
                    code.Add((byte)Instruction.MLOAD);
                    break;
                default:
                    PushWord(code, PatternWord(random));
                    break;
            }

            live++;
        }

        int start = random.Next(2, 12);
        for (int i = 0; i < start; i++) Word();
        int steps = random.Next(8, 90);
        for (int i = 0; i < steps; i++)
        {
            int choice = random.Next(100);
            if (ExtendedOps && live >= 1 && choice < 14)
            {
                // Bodies and helpers with out-of-line paths, their operands often copies of words still live below.
                int k = Math.Min(live, random.Next(1, 9));
                switch (random.Next(12))
                {
                    case 0:
                        // MLOAD and MSTORE where memory grows, so the out-of-line body runs.
                        PushWord(code, random.Next(0, 0x3000));
                        code.Add((byte)Instruction.MLOAD);
                        live++;
                        break;
                    case 1:
                        code.Add((byte)(Instruction.DUP1 + (byte)(k - 1)));
                        PushWord(code, random.Next(0, 0x3000));
                        code.Add((byte)Instruction.MSTORE);
                        break;
                    case 2:
                        code.Add((byte)(Instruction.DUP1 + (byte)(k - 1)));
                        PushWord(code, random.Next(0, 0x3000));
                        code.Add((byte)Instruction.MSTORE8);
                        break;
                    case 3:
                        PushWord(code, random.Next(0, 100));
                        PushWord(code, random.Next(0, 0x2000));
                        code.Add((byte)Instruction.KECCAK256);
                        live++;
                        break;
                    case 4:
                        // A log whose topic is a copy of a live word.
                        code.Add((byte)(Instruction.DUP1 + (byte)(k - 1)));
                        PushWord(code, random.Next(0, 64));
                        PushWord(code, random.Next(0, 0x500));
                        code.Add((byte)Instruction.LOG1);
                        break;
                    case 5:
                        code.Add((byte)(Instruction.DUP1 + (byte)(k - 1)));
                        PushWord(code, random.Next(0, 6));
                        code.Add((byte)Instruction.SSTORE);
                        break;
                    case 6:
                        code.Add((byte)(Instruction.DUP1 + (byte)(k - 1)));
                        code.Add(random.Next(3) switch { 0 => (byte)Instruction.BALANCE, 1 => (byte)Instruction.EXTCODESIZE, _ => (byte)Instruction.EXTCODEHASH });
                        live++;
                        break;
                    case 7:
                    {
                        // A call into the callee, its input a copy of a live word, then the return data.
                        code.Add((byte)(Instruction.DUP1 + (byte)(k - 1)));
                        PushWord(code, 0x200);
                        code.Add((byte)Instruction.MSTORE);
                        PushWord(code, random.Next(0, 64));
                        PushWord(code, random.Next(0, 0x600));
                        PushWord(code, 32);
                        PushWord(code, 0x200);
                        bool staticCall = random.Next(2) == 0;
                        if (!staticCall) code.Add((byte)Instruction.PUSH0);
                        code.Add((byte)Instruction.PUSH20);
                        code.AddRange(Callee.Bytes);
                        code.Add((byte)Instruction.GAS);
                        code.Add(staticCall ? (byte)Instruction.STATICCALL : (byte)Instruction.CALL);
                        live++;
                        if (random.Next(2) == 0)
                        {
                            code.Add((byte)Instruction.RETURNDATASIZE);
                            live++;
                        }
                        break;
                    }
                    case 8:
                        PushWord(code, random.Next(0, 33));
                        PushWord(code, random.Next(0, 300));
                        PushWord(code, random.Next(0, 0x2000));
                        code.Add(random.Next(2) == 0 ? (byte)Instruction.CALLDATACOPY : (byte)Instruction.MCOPY);
                        break;
                    case 9:
                        // CALLDATALOAD of a copied word, its offset out of range most of the time.
                        code.Add((byte)(Instruction.DUP1 + (byte)(k - 1)));
                        code.Add((byte)Instruction.CALLDATALOAD);
                        live++;
                        break;
                    case 10:
                        code.Add((byte)(Instruction.DUP1 + (byte)(k - 1)));
                        PushWord(code, random.Next(0, 300));
                        code.Add((byte)Instruction.AND);
                        code.Add((byte)Instruction.CALLDATALOAD);
                        live++;
                        break;
                    default:
                        code.Add((byte)(Instruction.DUP1 + (byte)(k - 1)));
                        PushWord(code, random.Next(0, 6));
                        code.Add((byte)Instruction.TSTORE);
                        PushWord(code, random.Next(0, 6));
                        code.Add((byte)Instruction.TLOAD);
                        live++;
                        break;
                }

                continue;
            }

            if (live < 3 || (choice < 10 && live < 60))
            {
                Word();
            }
            else if (choice < 26)
            {
                code.Add((byte)(Instruction.DUP1 + (byte)(Math.Min(live, random.Next(3) == 0 ? random.Next(1, 17) : random.Next(1, 5)) - 1)));
                live++;
            }
            else if (choice < 40)
            {
                code.Add((byte)(Instruction.SWAP1 + (byte)(Math.Min(live - 1, random.Next(3) == 0 ? random.Next(1, 17) : random.Next(1, 5)) - 1)));
            }
            else if (choice < 44)
            {
                code.Add((byte)Instruction.POP);
                live--;
            }
            else if (choice < 62)
            {
                // An operation with a constant, as Solidity masks, shifts and scales.
                PushWord(code, PatternWord(random));
                if (random.Next(3) == 0) code.Add((byte)Instruction.SWAP1);
                code.Add((byte)PatternBinary[random.Next(PatternBinary.Length)]);
            }
            else if (choice < 76)
            {
                code.Add((byte)PatternBinary[random.Next(PatternBinary.Length)]);
                live--;
            }
            else if (choice < 82)
            {
                code.Add(random.Next(4) switch { 0 => (byte)Instruction.NOT, 1 => (byte)Instruction.ISZERO, 2 => (byte)Instruction.CLZ, _ => (byte)Instruction.ISZERO });
            }
            else if (choice < 85)
            {
                code.Add(random.Next(2) == 0 ? (byte)Instruction.ADDMOD : (byte)Instruction.MULMOD);
                live -= 2;
            }
            else if (choice < 89)
            {
                // A word through memory: written at a fresh or an old offset, the offset kept by DUP sometimes.
                int at = random.Next(memoryWords + 1);
                PushWord(code, at * 32 + 0x400);
                code.Add((byte)Instruction.MSTORE);
                live--;
                memoryWords = Math.Max(memoryWords, at + 1);
            }
            else if (choice < 91)
            {
                PushWord(code, random.Next(0, 6));
                code.Add((byte)Instruction.SSTORE);
                live--;
            }
            else if (choice < 95 && !NoBranches)
            {
                // A branch on the top word to just past it: a segment ends whichever way it goes.
                code.Add((byte)Instruction.DUP1);
                code.Add((byte)Instruction.PUSH2);
                int fix = code.Count;
                code.Add(0);
                code.Add(0);
                code.Add((byte)Instruction.JUMPI);
                if (random.Next(2) == 0)
                {
                    // A word only the fall-through pushes, popped at the join so both paths leave the same depth.
                    Word();
                    code.Add((byte)Instruction.POP);
                    live--;
                }

                int dest = code.Count;
                code[fix] = (byte)(dest >> 8);
                code[fix + 1] = (byte)dest;
                code.Add((byte)Instruction.JUMPDEST);
            }
            else if (choice < 97 && !NoBranches)
            {
                code.Add((byte)Instruction.JUMPDEST);
            }
            else
            {
                // KECCAK256 of a word written first, as mapping slots are computed.
                PushWord(code, 0x300);
                code.Add((byte)Instruction.MSTORE);
                PushWord(code, 32);
                PushWord(code, 0x300);
                code.Add((byte)Instruction.KECCAK256);
            }

            if (live < 1)
            {
                Word();
            }
        }

        for (int i = 0; i < live; i++)
        {
            PushWord(code, i * 32);
            code.Add((byte)Instruction.MSTORE);
        }

        PushWord(code, live * 32);
        PushWord(code, 0);
        code.Add((byte)Instruction.RETURN);
        returned = live;
        return [.. code];
    }

    [TestCase(1, 3000)]
    [TestCase(2, 3000)]
    [TestCase(3, 3000)]
    [TestCase(4, 3000)]
    public void Vector_forms_match_the_interpreter_on_solidity_shapes(int seed, int programs)
    {
        bool saved = IlEvmSettings.VectorForms;
        // ILEVM_PATTERN_VECTOR=0 runs the same programs with the forms off, to tell their faults from others.
        IlEvmSettings.VectorForms = Environment.GetEnvironmentVariable("ILEVM_PATTERN_VECTOR") != "0";
        try
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("ILEVM_PATTERN_SEED_OFFSET"), out int offset)) seed += offset;
            Random random = new(seed);
            int count = int.TryParse(Environment.GetEnvironmentVariable("ILEVM_PATTERN_PROGRAMS"), out int n) ? n : programs;
            for (int i = 0; i < count; i++)
            {
                byte[] code = PatternProgram(random, out _);
                byte[] input = PatternInput(random);
                CodeInfo codeInfo = new(code);
                Outcome expected = Run(codeInfo, input, 10_000_000, compiledCode: false);
                Outcome actual = Run(codeInfo, input, 10_000_000, compiledCode: true);
                if (Same(expected, actual) && !expected.IsError && random.Next(4) == 0)
                {
                    // Out of gas on each of the last instructions, with whatever is virtual written out where it stops.
                    ulong used = 10_000_000 - expected.GasLeft;
                    for (ulong back = 1; back <= Math.Min(used, 40UL) && Same(expected, actual); back++)
                    {
                        expected = Run(codeInfo, input, used - back, compiledCode: false);
                        actual = Run(codeInfo, input, used - back, compiledCode: true);
                    }
                }

                Assert.That(Same(expected, actual), $"seed {seed} program {i}\ncode {code.ToHexString(true)}\ninput {input.ToHexString(true)}\ninterpreter {expected}\ncompiled    {actual}");
            }
        }
        finally
        {
            IlEvmSettings.VectorForms = saved;
        }
    }

    /// <summary>The instructions of <paramref name="code"/>, each with its immediate.</summary>
    private static List<byte[]> Instructions(byte[] code)
    {
        List<byte[]> list = [];
        for (int pc = 0; pc < code.Length;)
        {
            int size = 1 + (code[pc] is >= (byte)Instruction.PUSH1 and <= (byte)Instruction.PUSH32 ? code[pc] - (byte)Instruction.PUSH1 + 1 : 0);
            list.Add(code.AsSpan(pc, Math.Min(size, code.Length - pc)).ToArray());
            pc += size;
        }
        return list;
    }

    private bool VectorMismatch(byte[] code, byte[] input)
    {
        CodeInfo codeInfo = new(code);
        Outcome expected = Run(codeInfo, input, 10_000_000, compiledCode: false);
        if (expected.IsError) return false;
        Outcome actual = Run(codeInfo, input, 10_000_000, compiledCode: true);
        return !Same(expected, actual);
    }

    /// <summary>Shrinks a straight-line failing program (ILEVM_MIN_CODE, ILEVM_MIN_INPUT) one instruction at a time.</summary>
    [Test, Explicit("Minimizes a failing program")]
    public void Vector_forms_minimize()
    {
        bool saved = IlEvmSettings.VectorForms;
        IlEvmSettings.VectorForms = true;
        try
        {
            byte[] code = Bytes.FromHexString(Environment.GetEnvironmentVariable("ILEVM_MIN_CODE")!);
            byte[] input = Bytes.FromHexString(Environment.GetEnvironmentVariable("ILEVM_MIN_INPUT") ?? "");
            Assert.That(VectorMismatch(code, input), "does not fail");
            List<byte[]> ins = Instructions(code);
            bool progress = true;
            while (progress)
            {
                progress = false;
                for (int chunk = Math.Max(1, ins.Count / 2); chunk >= 1; chunk /= 2)
                {
                    for (int i = 0; i + chunk <= ins.Count;)
                    {
                        List<byte[]> trial = [.. ins];
                        trial.RemoveRange(i, chunk);
                        byte[] candidate = [.. System.Linq.Enumerable.SelectMany(trial, static x => x)];
                        if (VectorMismatch(candidate, input)) { ins = trial; progress = true; }
                        else i++;
                    }
                }
                // Inputs: zero each word that is not needed.
                for (int w = 0; w * 32 < input.Length; w++)
                {
                    byte[] trial = (byte[])input.Clone();
                    Array.Clear(trial, w * 32, Math.Min(32, trial.Length - w * 32));
                    if (!trial.AsSpan().SequenceEqual(input) && VectorMismatch([.. System.Linq.Enumerable.SelectMany(ins, static x => x)], trial)) { input = trial; progress = true; }
                }
            }
            byte[] min = [.. System.Linq.Enumerable.SelectMany(ins, static x => x)];
            CodeInfo info = new(min);
            TestContext.Out.WriteLine($"MIN code {min.ToHexString(true)} MIN input {input.ToHexString(true)}");
            TestContext.Out.WriteLine($"interpreter {Run(info, input, 10_000_000, false)} COMPILED {Run(info, input, 10_000_000, true)}");
        }
        finally
        {
            IlEvmSettings.VectorForms = saved;
        }
    }
}
