// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>Random programs of the stack opcodes through every host table, for the register-dispatch prototype.</summary>
/// <remarks>
/// The programs mix DUP, SWAP, POP, PUSH0-PUSH32 and ADD with PUSH2+JUMP and PUSH2+JUMPI shapes and a few opcodes the
/// prototype leaves on the write-back adapter (MUL, JUMPDEST, GAS, PC, MSTORE, MLOAD), from stacks that are empty, shallow
/// or at the limit, on gas that runs out part-way. Untraced and traced tables must agree as the other tests here require.
/// With <c>HOST_DISPATCH_DUMP</c> set, each untraced outcome is also written to that file, so that the same sweep on
/// another build - the base the prototype starts from - can be compared line by line: fault, gas, program counter,
/// head, stack, memory and opcode count.
/// </remarks>
public partial class HostMemoryFastPathTests
{
    private const int StackProgramsPerSeed = 3000;
    private const ulong StackProgramGas = 20_000;

    private static readonly object DumpLock = new();

    private static IEnumerable<TestCaseData> StackSeeds()
    {
        for (int seed = 0; seed < 8; seed++)
            yield return new TestCaseData(seed).SetName($"{{m}}({seed})");
    }

    [TestCaseSource(nameof(StackSeeds))]
    public void Random_stack_programs_agree_across_tables(int seed)
    {
        Harness harness = new();
        List<string> mismatches = [];
        List<string> dump = [];
        for (int i = 0; i < StackProgramsPerSeed && mismatches.Count < 5; i++)
        {
            Random random = new(seed * 1_000_033 + i);
            byte[] code = GenerateStackProgram(random);
            byte[] stack = StackOfDepth(random, random.Next(6) switch
            {
                0 => 0,
                1 => random.Next(1, 4),
                2 => random.Next(0, 24),
                3 => random.Next(EvmStack.MaxStackSize - 20, EvmStack.MaxStackSize),
                4 => EvmStack.MaxStackSize - 1,
                _ => random.Next(16, 40),
            });
            ulong gas = random.Next(4) switch
            {
                0 => (ulong)random.Next(0, 12),
                1 => (ulong)random.Next(0, 60),
                2 => (ulong)random.Next(0, 400),
                _ => StackProgramGas,
            };

            for (int pass = 0; pass < 4; pass++)
            {
                if (pass >= 2)
                {
                    // Gas boundaries: exactly what an untraced run uses, then one short of it.
                    Outcome full = harness.Run(code, StackProgramGas, Table.NoTrace, Setup.Fresh, [], stack);
                    if (IsFault(full.Exception)) break;
                    ulong used = StackProgramGas - full.GasLeft;
                    gas = used - Math.Min(used, (ulong)(pass - 2));
                }
                else if (pass == 1)
                {
                    gas = gas * 3 + 7;
                }

                harness.Compare(code, gas, Setup.Fresh, [], mismatches, stack);
                Outcome noTrace = harness.Run(code, gas, Table.NoTrace, Setup.Fresh, [], stack);
                Outcome cancelable = harness.Run(code, gas, Table.NoTraceCancelable, Setup.Fresh, [], stack);
                dump.Add($"{seed} {i} {pass} {Convert.ToHexString(code)} d{stack.Length / EvmStack.WordSize} g{gas} | {Describe(noTrace)} | {Describe(cancelable)}");
            }
        }

        string? path = Environment.GetEnvironmentVariable("HOST_DISPATCH_DUMP");
        if (!string.IsNullOrEmpty(path))
        {
            lock (DumpLock)
                File.AppendAllLines(path, dump);
        }

        Assert.That(mismatches, Is.Empty);
    }

    private static string Describe(Outcome outcome) =>
        $"{outcome.Exception} gas {outcome.GasLeft} pc {outcome.Pc} head {outcome.Head} stack {Digest(outcome.Stack)} mem {Digest(outcome.Memory)}/{outcome.MemorySize} ops {outcome.OpCodeCount}";

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(value)), 0, 8);

    private static byte[] StackOfDepth(Random random, int depth)
    {
        byte[] stack = new byte[depth * EvmStack.WordSize];
        for (int at = 0; at < stack.Length; at += EvmStack.WordSize)
        {
            // Limb layout: byte 0 is the lowest byte of the word.
            switch (random.Next(5))
            {
                case 0: break;
                case 1: stack[at] = 1; break;
                case 2: stack[at] = (byte)random.Next(256); stack[at + 1] = (byte)random.Next(0, 2); break;
                case 3: stack.AsSpan(at, EvmStack.WordSize).Fill(0xff); break;
                default: random.NextBytes(stack.AsSpan(at, EvmStack.WordSize)); break;
            }
        }

        return stack;
    }

    /// <summary>A random program of the ported stack opcodes, with PUSH2 jumps to JUMPDESTs it holds or to bad destinations.</summary>
    private static byte[] GenerateStackProgram(Random random)
    {
        List<byte> code = [];
        List<int> jumpDestinations = [];
        List<int> jumpImmediates = [];
        int length = random.Next(1, 48);
        for (int k = 0; k < length; k++)
        {
            int pick = random.Next(100);
            if (pick < 18)
            {
                code.Add((byte)((byte)Instruction.DUP1 + random.Next(random.Next(4) == 0 ? 16 : 3)));
            }
            else if (pick < 34)
            {
                code.Add((byte)((byte)Instruction.SWAP1 + random.Next(random.Next(4) == 0 ? 16 : 3)));
            }
            else if (pick < 44)
            {
                code.Add(POP);
            }
            else if (pick < 50)
            {
                code.Add(PUSH0);
            }
            else if (pick < 64)
            {
                int size = random.Next(3) == 0 ? random.Next(1, 33) : random.Next(1, 3);
                code.Add((byte)((byte)Instruction.PUSH1 + size - 1));
                for (int b = 0; b < size; b++) code.Add((byte)(random.Next(3) == 0 ? 0xff : random.Next(4)));
            }
            else if (pick < 74)
            {
                code.Add(ADD);
            }
            else if (pick < 84)
            {
                // PUSH2 dest JUMP or JUMPI; the destination is patched below.
                code.Add(PUSH2);
                jumpImmediates.Add(code.Count);
                code.Add(0);
                code.Add(0);
                code.Add(random.Next(2) == 0 ? JUMP : JUMPI);
            }
            else if (pick < 90)
            {
                jumpDestinations.Add(code.Count);
                code.Add(JUMPDEST);
            }
            else
            {
                code.Add(random.Next(6) switch
                {
                    0 => (byte)Instruction.MUL,
                    1 => GAS,
                    2 => (byte)Instruction.PC,
                    3 => MSTORE,
                    4 => MLOAD,
                    _ => STOP,
                });
            }
        }

        // Occasionally end on a push whose immediate runs past the code.
        if (random.Next(8) == 0)
        {
            int size = random.Next(1, 33);
            code.Add((byte)((byte)Instruction.PUSH1 + size - 1));
            for (int b = random.Next(0, size); b > 0; b--) code.Add((byte)random.Next(256));
        }

        foreach (int at in jumpImmediates)
        {
            int destination = jumpDestinations.Count != 0 && random.Next(5) != 0
                ? jumpDestinations[random.Next(jumpDestinations.Count)]
                : random.Next(3) switch { 0 => random.Next(0, code.Count + 2), 1 => 0xffff, _ => at + 2 };
            code[at] = (byte)(destination >> 8);
            code[at + 1] = (byte)destination;
        }

        return code.ToArray();
    }
}
