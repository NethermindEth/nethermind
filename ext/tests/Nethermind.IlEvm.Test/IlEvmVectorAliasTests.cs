// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited

using System;
using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using NUnit.Framework;

namespace Nethermind.IlEvm.Test;

/// <summary>
/// Random programs, vector forms on, built to leave a copy of a word below the word itself (DUP, a SWAP that sinks the copy,
/// POP), the word in its slot or a local, and then hand the word to every kind of consumer: bodies that rewrite their input
/// slots in place (MSTORE growing memory, LOG topics, BALANCE and CALL addresses, CREATE2 salts), helpers that write the
/// second slot, constant forms, branches and segment exits; and many live words, so resolving an alias spills the locals.
/// </summary>
public partial class IlEvmDifferentialTests
{
    private static byte[] AliasProgram(Random random)
    {
        List<byte> code = [];
        int live = 0;

        void Push(ulong v)
        {
            if (v == 0) { code.Add((byte)Instruction.PUSH0); return; }
            byte[] bytes = BitConverter.GetBytes(v);
            Array.Reverse(bytes);
            int skip = 0;
            while (bytes[skip] == 0) skip++;
            code.Add((byte)(Instruction.PUSH1 + (byte)(8 - skip - 1)));
            for (int i = skip; i < 8; i++) code.Add(bytes[i]);
        }

        void Op(Instruction i) => code.Add((byte)i);

        // Memory and storage filled from the input, so MLOAD and SLOAD give words with every limb in use.
        Push(0x200); Push(0); Push(0); Op(Instruction.CALLDATACOPY);
        for (ulong s = 0; s < 6; s++) { Push(s * 32); Op(Instruction.CALLDATALOAD); Push(s); Op(Instruction.SSTORE); }

        // A word whose value lives in its slot (a body's result), in a vector local, a constant or a flag.
        void Word()
        {
            switch (random.Next(9))
            {
                case 0 or 1: Push((ulong)random.Next(6)); Op(Instruction.SLOAD); break;
                case 2 or 3: Push((ulong)random.Next(0, 0x1e0)); Op(Instruction.MLOAD); break;
                case 4: Push((ulong)random.Next(0, 0x120)); Op(Instruction.CALLDATALOAD); break;
                case 5: Push((ulong)random.Next(0, 0x120)); Op(Instruction.CALLDATALOAD); Op(Instruction.NOT); break;
                case 6: Push((ulong)random.Next(6)); Op(Instruction.SLOAD); Push((ulong)random.Next(1, 256)); Op(Instruction.SHL); break;
                case 7: Op(Instruction.GAS); break;
                default: Push(random.Next(2) == 0 ? (ulong)random.Next(0, 300) : (ulong)random.NextInt64()); break;
            }
            live++;
        }

        // DUP1, a SWAP that sinks the copy, POP: the word stays where it was with an alias of its slot below.
        void Sink()
        {
            if (live < 2) return;
            Op(Instruction.DUP1);
            Op((Instruction)((byte)Instruction.SWAP1 + random.Next(Math.Min(live, 16))));
            Op(Instruction.POP);
        }

        int start = random.Next(3, random.Next(4) == 0 ? 60 : 14);
        for (int i = 0; i < start; i++) Word();
        int steps = random.Next(10, 70);
        for (int step = 0; step < steps; step++)
        {
            int choice = random.Next(100);
            if (live < 3 || (choice < 12 && live < 70)) { Word(); continue; }
            if (choice < 30) { Sink(); if (random.Next(2) == 0) Sink(); continue; }
            if (choice < 40) { Op((Instruction)((byte)Instruction.DUP1 + random.Next(Math.Min(live, 16)))); live++; continue; }
            if (choice < 48) { Op((Instruction)((byte)Instruction.SWAP1 + random.Next(Math.Min(live - 1, 16)))); continue; }
            if (choice < 51) { Op(Instruction.POP); live--; continue; }

            // A consumer of the top word, or of the word under constants pushed now.
            switch (random.Next(22))
            {
                case 0: Push(random.Next(2) == 0 ? (ulong)random.Next(0, 0x200) : (ulong)random.Next(0x200, 0x3000)); Op(Instruction.MSTORE); live--; break;
                case 1: Push((ulong)random.Next(0, 0x3000)); Op(Instruction.MSTORE8); live--; break;
                case 2: Push((ulong)random.Next(0, 6)); Op(Instruction.SSTORE); live--; break;
                case 3: Push((ulong)random.Next(0, 6)); Op(Instruction.TSTORE); live--; break;
                case 4:
                {
                    int topics = Math.Min(live, random.Next(1, 5));
                    Push((ulong)random.Next(0, 64)); Push((ulong)random.Next(0, 0x400));
                    Op((Instruction)((byte)Instruction.LOG0 + topics)); live -= topics;
                    break;
                }
                case 5: Op(random.Next(3) switch { 0 => Instruction.BALANCE, 1 => Instruction.EXTCODESIZE, _ => Instruction.EXTCODEHASH }); break;
                case 6: Op(Instruction.CALLDATALOAD); break;
                case 7: Push(0x1fff); Op(Instruction.AND); Op(Instruction.MLOAD); break;
                case 8: Op(random.Next(3) switch { 0 => Instruction.NOT, 1 => Instruction.ISZERO, _ => Instruction.CLZ }); break;
                case 9: Op(Instruction.SLOAD); break;
                case 10: Op(Instruction.TLOAD); break;
                case 11:
                    Op(random.Next(12) switch
                    {
                        0 => Instruction.ADD, 1 => Instruction.SUB, 2 => Instruction.MUL, 3 => Instruction.DIV, 4 => Instruction.EXP,
                        5 => Instruction.BYTE, 6 => Instruction.SIGNEXTEND, 7 => Instruction.SAR, 8 => Instruction.SLT,
                        9 => Instruction.AND, 10 => Instruction.XOR, _ => Instruction.EQ,
                    });
                    live--;
                    break;
                case 12:
                    Push(random.Next(2) == 0 ? (ulong)random.Next(0, 300) : (ulong)random.NextInt64());
                    if (random.Next(2) == 0) Op(Instruction.SWAP1);
                    Op(random.Next(10) switch
                    {
                        0 => Instruction.ADD, 1 => Instruction.SUB, 2 => Instruction.MUL, 3 => Instruction.DIV, 4 => Instruction.MOD,
                        5 => Instruction.SHL, 6 => Instruction.SHR, 7 => Instruction.LT, 8 => Instruction.GT, _ => Instruction.OR,
                    });
                    break;
                case 13: Op(random.Next(2) == 0 ? Instruction.ADDMOD : Instruction.MULMOD); live -= 2; if (live < 1) Word(); break;
                case 14:
                {
                    // A call whose address is the top word: STATICCALL gas, addr, in, inSize, out, outSize.
                    Push(32); Push(0x40); Push(32); Push(0);
                    Op(Instruction.DUP5);
                    Op(Instruction.GAS);
                    Op(Instruction.STATICCALL);
                    live++;
                    if (random.Next(2) == 0) { Op(Instruction.RETURNDATASIZE); live++; }
                    break;
                }
                case 15:
                    // CREATE2 whose salt is the top word, of an empty init code.
                    Push(0); Push(0); Push(0); Op(Instruction.CREATE2); break;
                case 16:
                    // KECCAK256 of the word written first, as mapping slots are.
                    Push(0x300); Op(Instruction.MSTORE); Push(32); Push(0x300); Op(Instruction.KECCAK256); break;
                case 17:
                {
                    // A branch on the top word to just past it: the segment ends whichever way it goes.
                    Op(Instruction.DUP1);
                    Op(Instruction.PUSH2);
                    int fix = code.Count;
                    code.Add(0); code.Add(0);
                    Op(Instruction.JUMPI);
                    if (random.Next(2) == 0) { Word(); Op(Instruction.POP); live--; }
                    int dest = code.Count;
                    code[fix] = (byte)(dest >> 8); code[fix + 1] = (byte)dest;
                    Op(Instruction.JUMPDEST);
                    break;
                }
                case 18: Op(Instruction.JUMPDEST); break;
                case 19: Push(0x20); Push((ulong)random.Next(0, 0x100)); Push((ulong)random.Next(0, 0x2000)); Op(Instruction.MCOPY); break;
                case 20: Push((ulong)random.Next(0, 0x2000)); Op(Instruction.MSTORE); Push((ulong)random.Next(0, 0x2000)); Op(Instruction.MLOAD); break;
                default: Op(Instruction.DUP1); Push((ulong)random.Next(0, 0x2000)); Op(Instruction.MSTORE); break;
            }
            if (live < 1) Word();
        }

        for (int i = 0; i < live; i++) { Push((ulong)(0x4000 + i * 32)); Op(Instruction.MSTORE); }
        Push((ulong)(live * 32)); Push(0x4000); Op(Instruction.RETURN);
        return [.. code];
    }

    [TestCase(1, 2000)]
    [TestCase(2, 2000)]
    [TestCase(3, 2000)]
    public void Vector_aliases_below_their_words_match_the_interpreter(int seed, int programs)
    {
        bool saved = IlEvmSettings.VectorForms;
        IlEvmSettings.VectorForms = Environment.GetEnvironmentVariable("ILEVM_PATTERN_VECTOR") != "0";
        try
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("ILEVM_PATTERN_SEED_OFFSET"), out int offset)) seed += offset;
            int count = int.TryParse(Environment.GetEnvironmentVariable("ILEVM_PATTERN_PROGRAMS"), out int n) ? n : programs;
            Random random = new(seed * 7919);
            for (int i = 0; i < count; i++)
            {
                byte[] code = AliasProgram(random);
                byte[] input = PatternInput(random);
                CodeInfo codeInfo = new(code);
                Outcome expected = Run(codeInfo, input, 10_000_000, compiledCode: false);
                Outcome actual = Run(codeInfo, input, 10_000_000, compiledCode: true);
                if (Same(expected, actual) && !expected.IsError && random.Next(4) == 0)
                {
                    ulong used = 10_000_000 - expected.GasLeft;
                    for (ulong back = 1; back <= Math.Min(used, 30UL) && Same(expected, actual); back++)
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
}
