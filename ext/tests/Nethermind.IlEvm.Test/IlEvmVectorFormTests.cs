// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using NUnit.Framework;

namespace Nethermind.IlEvm.Test;

/// <summary>Programs that reach the vector forms' alias and spill bookkeeping.</summary>
public partial class IlEvmDifferentialTests
{
    private sealed class VectorProgram
    {
        private readonly List<byte> _b = [];
        public VectorProgram Op(Instruction i) { _b.Add((byte)i); return this; }
        public VectorProgram Push(ulong v)
        {
            if (v == 0) { _b.Add((byte)Instruction.PUSH0); return this; }
            byte[] bytes = BitConverter.GetBytes(v).Reverse().SkipWhile(static x => x == 0).ToArray();
            _b.Add((byte)(0x5f + bytes.Length));
            _b.AddRange(bytes);
            return this;
        }
        public VectorProgram Repeat(int n, Action<VectorProgram> a) { for (int i = 0; i < n; i++) a(this); return this; }
        public byte[] Code => [.. _b];
    }

    /// <summary>Runs <paramref name="code"/> on both machines with the vector forms on, whatever the default.</summary>
    private void CheckVectorProgram(string name, byte[] code, byte[] input, ulong gas = 1_000_000)
    {
        bool saved = IlEvmSettings.VectorForms;
        IlEvmSettings.VectorForms = true;
        try
        {
            CodeInfo codeInfo = new(code);
            Outcome expected = Run(codeInfo, input, gas, compiledCode: false);
            Outcome actual = Run(codeInfo, input, gas, compiledCode: true);
            Assert.That(IlEvm.IsCompiled(codeInfo), IlEvm.FailureReason(codeInfo));
            Assert.That(Same(expected, actual), $"{name}\ncode {code.ToHexString(true)}\ninterpreter {expected}\ncompiled    {actual}");
        }
        finally
        {
            IlEvmSettings.VectorForms = saved;
        }
    }

    /// <summary>
    /// MSTORE whose memory grows runs its body out of line, which turns the word's slot to memory byte order in place; a
    /// copy of the word left below by DUP and SWAP aliased that slot and was stored byte-reversed (the runner's 154 corpus
    /// mismatches, through an Ekubo tick-data lens).
    /// </summary>
    [Test]
    public void Vector_mstore_body_keeps_an_alias_of_its_word()
    {
        VectorProgram a = new();
        a.Push(32);                                 // p0, the size returned
        a.Push(0x80);                               // p1
        a.Push(0).Op(Instruction.CALLDATALOAD);     // p2 X in its slot
        a.Op(Instruction.DUP1);                     // p3 alias(p2)
        a.Op(Instruction.SWAP2);                    // p1 alias(p2), p3 0x80
        a.Op(Instruction.POP);
        a.Push(0x400).Op(Instruction.MSTORE);       // memory grows: the body stores X, swapping slot p2
        a.Push(0).Op(Instruction.MSTORE);           // mem[0] = p1 = X
        a.Push(0).Op(Instruction.RETURN);
        CheckVectorProgram("mstore body alias", a.Code, CallDataWords(1));
    }

    /// <summary>The program the fuzzer found first (seed 4), minimized: the same alias with the word's copy two below it.</summary>
    [Test]
    public void Vector_mstore_body_keeps_an_alias_of_its_word_minimized()
    {
        byte[] code = Bytes.FromHexString("0x61f06160805f3560a0819250506104005260c0525ff3");
        byte[] input = Bytes.FromHexString("0x0000000000000200000000000000000000000000000000000000000000000000");
        CheckVectorProgram("mstore body alias, minimized", code, input);
    }

    private static byte[] CallDataWords(int words)
    {
        byte[] input = new byte[words * 32];
        for (int i = 0; i < input.Length; i++) input[i] = (byte)(0x11 + i);
        return input;
    }

    /// <summary>48 GAS words (all MaxVecLocals in use) with the first-declared vector local at position 47.</summary>
    private static VectorProgram FortyEightVectorsFirstOnTop()
    {
        VectorProgram a = new();
        a.Repeat(17, static x => x.Op(Instruction.GAS));
        a.Op(Instruction.SWAP16);
        a.Repeat(16, static x => x.Op(Instruction.GAS));
        a.Op(Instruction.SWAP16);
        a.Repeat(15, static x => x.Op(Instruction.GAS));
        a.Op(Instruction.SWAP15);
        return a;
    }

    [Test]
    public void Vector_swap_resolving_aliases_with_every_local_in_use()
    {
        VectorProgram a = FortyEightVectorsFirstOnTop();
        a.Push(0).Op(Instruction.CALLDATALOAD); // p48 in slot X
        a.Op(Instruction.DUP1);                 // p49 alias of p48
        a.Op(Instruction.SWAP2);                // p47 alias of p48, p49 = L0
        a.Op(Instruction.SWAP1);                // p48 takes L0's word; resolving p47 spills and reuses L0
        a.Op(Instruction.POP);
        a.Push(0).Op(Instruction.MSTORE);       // mem[0] = p48, the first GAS word
        a.Push(32).Push(0).Op(Instruction.RETURN);
        CheckVectorProgram("swap spill", a.Code, CallDataWords(3));
    }

    [Test]
    public void Vector_dup_resolving_aliases_with_every_local_in_use()
    {
        VectorProgram a = FortyEightVectorsFirstOnTop();
        a.Push(0).Op(Instruction.CALLDATALOAD);    // p48 X slot
        a.Op(Instruction.DUP1);                    // p49 alias(p48)
        a.Push(32).Op(Instruction.CALLDATALOAD);   // p50 Y slot
        a.Push(64).Op(Instruction.CALLDATALOAD);   // p51 Z slot
        a.Op(Instruction.DUP1);                    // p52 alias(p51)
        a.Op(Instruction.SWAP3);                   // p49 alias(p51), p52 alias(p48)
        a.Op(Instruction.POP);
        a.Op(Instruction.ADD);                     // p50 = Z + Y; p51 dead, aliased by p49
        a.Op(Instruction.DUP4);                    // p51 = L0's word; resolving p49 spills and reuses L0
        a.Push(0).Op(Instruction.MSTORE);          // mem[0] = p51, the first GAS word
        a.Push(32).Op(Instruction.MSTORE);
        a.Push(64).Op(Instruction.MSTORE);
        a.Push(96).Push(0).Op(Instruction.RETURN);
        CheckVectorProgram("dup spill", a.Code, CallDataWords(3));
    }

    [Test]
    public void Vector_dup_of_an_alias_onto_its_own_source_slot()
    {
        VectorProgram a = new();
        a.Push(0).Op(Instruction.CALLDATALOAD);    // p0 X slot
        a.Op(Instruction.DUP1);                    // p1 alias(p0)
        a.Push(32).Op(Instruction.CALLDATALOAD);   // p2 Y slot
        a.Push(64).Op(Instruction.CALLDATALOAD);   // p3 Z slot
        a.Op(Instruction.DUP1);                    // p4 alias(p3)
        a.Op(Instruction.SWAP3);                   // p1 alias(p3), p4 alias(p0)
        a.Op(Instruction.POP);
        a.Op(Instruction.ADD);                     // p2 = Y+Z; p3 dead, aliased by p1
        a.Op(Instruction.DUP2);                    // p3 = alias(p3) itself (p1 resolved)
        a.Op(Instruction.SWAP2);                   // p1 = alias(p3) while p3 is virtual
        a.Op(Instruction.SWAP1);                   // p3 takes Y+Z, no resolution of p1
        a.Op(Instruction.ADD);                     // helper path writes slot p3 = Y+Z first
        a.Push(0).Op(Instruction.MSTORE);
        a.Push(32).Op(Instruction.MSTORE);         // mem[32] = Z
        a.Push(64).Push(0).Op(Instruction.RETURN);
        CheckVectorProgram("self alias", a.Code, CallDataWords(3));
    }

    /// <summary>
    /// Random straight-line programs of vector-form operations under heavy DUP and SWAP shuffling, with up to about 70 live
    /// words so the vector locals run out and spill; every word left is returned.
    /// </summary>
    [TestCase(1, 1000)]
    [TestCase(2, 1000)]
    [TestCase(3, 1000)]
    public void Vector_stack_shuffles_match_the_interpreter(int seed, int programs)
    {
        bool saved = IlEvmSettings.VectorForms;
        IlEvmSettings.VectorForms = true;
        try
        {
            ShufflePrograms(seed, programs);
        }
        finally
        {
            IlEvmSettings.VectorForms = saved;
        }
    }

    private void ShufflePrograms(int seed, int programs)
    {
        Random random = new(seed);
        byte[] input = CallDataWords(4);
        for (int i = 0; i < programs; i++)
        {
            byte[] code = Shuffle(random, out int depth);
            CodeInfo codeInfo = new(code);
            Outcome expected = Run(codeInfo, input, 1_000_000, compiledCode: false);
            Outcome actual = Run(codeInfo, input, 1_000_000, compiledCode: true);
            Assert.That(IlEvm.IsCompiled(codeInfo), IlEvm.FailureReason(codeInfo));
            Assert.That(Same(expected, actual), $"seed {seed} program {i} depth {depth}\ncode {code.ToHexString(true)}\ninterpreter {expected}\ncompiled    {actual}");
        }
    }

    private static byte[] Shuffle(Random random, out int returned)
    {
        VectorProgram a = new();
        bool wide = random.Next(2) == 0;
        int target = wide ? random.Next(48, 72) : random.Next(3, 10);
        int live = 0;
        void Word()
        {
            switch (wide && random.Next(4) != 0 ? 0 : random.Next(8))
            {
                case 0 or 1 or 2: a.Op(Instruction.GAS); break;
                case 3 or 4 or 5: a.Push((ulong)random.Next(4) * 32).Op(Instruction.CALLDATALOAD); break;
                case 6: a.Push((ulong)random.Next(3)); break;
                default: a.Push((ulong)random.NextInt64()); break;
            }

            live++;
        }

        for (int i = 0; i < target; i++) Word();
        int steps = random.Next(10, 80);
        for (int i = 0; i < steps; i++)
        {
            int choice = random.Next(100);
            if (live < 2 || (choice < 8 && live < 80))
            {
                Word();
            }
            else if (choice < 35)
            {
                int n = Depth(random, live);
                a.Op((Instruction)((int)Instruction.DUP1 + n - 1));
                live++;
            }
            else if (choice < 65)
            {
                int n = Depth(random, live - 1);
                a.Op((Instruction)((int)Instruction.SWAP1 + n - 1));
            }
            else if (choice < 72)
            {
                a.Op(Instruction.POP);
                live--;
            }
            else if (choice < 88)
            {
                Instruction[] binary = [Instruction.ADD, Instruction.SUB, Instruction.ADD, Instruction.SUB, Instruction.ADD, Instruction.SUB, Instruction.AND, Instruction.OR, Instruction.XOR, Instruction.LT, Instruction.GT, Instruction.EQ];
                a.Op(binary[random.Next(binary.Length)]);
                live--;
            }
            else
            {
                switch (random.Next(6))
                {
                    case 0: a.Op(Instruction.NOT); break;
                    case 1: a.Op(Instruction.ISZERO); break;
                    case 2: a.Push((ulong)random.Next(257)).Op(Instruction.SHL); break;
                    case 3: a.Push((ulong)random.Next(257)).Op(Instruction.SHR); break;
                    case 4: a.Push((ulong)random.Next(1, 300)).Op(Instruction.ADD); break;
                    default: a.Push(0x7f).Op(Instruction.AND).Op(Instruction.CALLDATALOAD); break;
                }
            }
        }

        for (int i = 0; i < live; i++) a.Push((ulong)i * 32).Op(Instruction.MSTORE);
        a.Push((ulong)live * 32).Push(0).Op(Instruction.RETURN);
        returned = live;
        return a.Code;
    }

    /// <summary>A DUP or SWAP depth up to 16 and <paramref name="limit"/>, mostly near the top, where the shuffles overlap.</summary>
    private static int Depth(Random random, int limit)
    {
        int n = random.Next(3) == 0 ? 1 + random.Next(16) : 1 + random.Next(4);
        return Math.Min(n, limit);
    }
}
