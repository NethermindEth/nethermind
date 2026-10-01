// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;
using DispatchState = Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.DispatchState;

namespace Nethermind.Evm.Test;

/// <summary>Runs bytecode through the host's untraced tables, whose MLOAD and MSTORE try a frameless fast path first.</summary>
/// <remarks>
/// Each run is compared with the same table's plain handlers, which make up its fallback half and are the handlers every
/// table held before the fast paths, and with the traced table: the fault, gas left, program counter, stack, memory, its
/// size and the opcode count must agree. The cases sit where a fast path chooses - the active size, the initialized size,
/// the end of the inline memory and of a pooled array - over memory that is fresh or holds stale bytes, and each case
/// that succeeds also runs with every smaller amount of gas. The chain is entered the way the dispatch loop enters it.
/// </remarks>
[Parallelizable(ParallelScope.All)]
public class HostMemoryFastPathTests
{
    private const byte STOP = (byte)Instruction.STOP;
    private const byte ADD = (byte)Instruction.ADD;
    private const byte SUB = (byte)Instruction.SUB;
    private const byte KECCAK256 = (byte)Instruction.KECCAK256;
    private const byte CALLDATALOAD = (byte)Instruction.CALLDATALOAD;
    private const byte POP = (byte)Instruction.POP;
    private const byte MLOAD = (byte)Instruction.MLOAD;
    private const byte MSTORE = (byte)Instruction.MSTORE;
    private const byte MSTORE8 = (byte)Instruction.MSTORE8;
    private const byte JUMP = (byte)Instruction.JUMP;
    private const byte JUMPI = (byte)Instruction.JUMPI;
    private const byte MSIZE = (byte)Instruction.MSIZE;
    private const byte GAS = (byte)Instruction.GAS;
    private const byte JUMPDEST = (byte)Instruction.JUMPDEST;
    private const byte MCOPY = (byte)Instruction.MCOPY;
    private const byte PUSH0 = (byte)Instruction.PUSH0;
    private const byte PUSH1 = (byte)Instruction.PUSH1;
    private const byte PUSH2 = (byte)Instruction.PUSH2;
    private const byte PUSH4 = (byte)Instruction.PUSH4;
    private const byte PUSH5 = (byte)Instruction.PUSH5;
    private const byte PUSH32 = (byte)Instruction.PUSH32;
    private const byte DUP1 = (byte)Instruction.DUP1;
    private const byte DUP2 = (byte)Instruction.DUP2;
    private const byte SWAP1 = (byte)Instruction.SWAP1;
    private const byte RETURN = (byte)Instruction.RETURN;
    private const byte REVERT = (byte)Instruction.REVERT;

    private const ulong AmpleGas = 1_000_000;
    /// <summary>The most gas a random program gets: enough to grow memory far, little enough to bound its loops.</summary>
    private const ulong ProgramGas = 200_000;
    private const int ProgramsPerFork = 4000;

    private static readonly IReleaseSpec ReleaseSpec = Osaka.Instance;
    private static readonly BlockHeader Header = new(Hash256.Zero, Hash256.Zero, Address.Zero, UInt256.Zero, 1, 30_000_000, 1, []);
    private static readonly FieldInfo ThreadCacheField = typeof(EvmPooledMemory).GetField("_threadCache", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("EvmPooledMemory's thread cache was renamed or removed.");

    private static readonly byte[] WordA = Enumerable.Range(1, 32).Select(static b => (byte)b).ToArray();
    private static readonly byte[] WordB = Enumerable.Range(0xe0, 32).Select(static b => (byte)b).ToArray();

    [ThreadStatic] private static int _fallbacks;
    [ThreadStatic] private static nint[]? _plainHandlers;

    /// <summary>The dispatch tables a run can enter.</summary>
    public enum Table
    {
        /// <summary>The untraced table, with its fast paths.</summary>
        NoTrace,
        /// <summary>The untraced cancelable table, with its fast paths.</summary>
        NoTraceCancelable,
        /// <summary>The untraced table's fallback half: every handler plain.</summary>
        PlainNoTrace,
        /// <summary>The untraced cancelable table's fallback half.</summary>
        PlainNoTraceCancelable,
        /// <summary>The traced table, which has no fast paths.</summary>
        Traced,
    }

    /// <summary>The frame memory a run starts on.</summary>
    public enum Setup
    {
        /// <summary>A new frame's: zeroed inline memory, all of it initialized.</summary>
        Fresh,
        /// <summary>A reused frame's: inline memory full of stale bytes, none of it initialized.</summary>
        DirtyInline,
        /// <summary>As <see cref="DirtyInline"/>, and the pooled array memory spills into holds stale bytes too.</summary>
        DirtyArray,
        /// <summary>As <see cref="DirtyArray"/>, with 4096 bytes already active and none of them initialized.</summary>
        GrownDirtyArray,
    }

    private static readonly Table[] Tables = Enum.GetValues<Table>();

    internal readonly record struct Outcome(
        EvmExceptionType Exception, ulong GasLeft, nint Pc, nint Head, string Stack, string Memory, ulong MemorySize, nint OpCodeCount);

    private static IEnumerable<TestCaseData> Cases()
    {
        byte[] twoWords = [PUSH32, .. WordA, PUSH1, 0, MSTORE, PUSH32, .. WordB, PUSH1, 32, MSTORE];
        foreach (Setup setup in Enum.GetValues<Setup>())
        {
            // Two words active: loads at the active size less 32, less 31 and at it, and at the initialized size less 1 and plus 1.
            foreach (byte offset in (byte[])[31, 32, 33, 64])
                yield return Case($"MLOAD at {offset} of 64 active bytes", setup, [.. twoWords, PUSH1, offset, MLOAD, PUSH1, 0, MSTORE, STOP]);

            // Past the initialized prefix the chunked zeroing of the slow path leaves: 256 bytes of inline memory.
            foreach (ushort offset in (ushort[])[1, 224, 225, 992])
                yield return Case($"MLOAD at {offset} after a store at 0", setup, [PUSH32, .. WordA, PUSH1, 0, MSTORE, .. Push2(offset), MLOAD, PUSH1, 0, MSTORE, MSIZE, STOP]);

            // The end of the inline memory and the spill into a pooled array.
            foreach (ushort offset in (ushort[])[960, 992, 993, 1000, 1024, 1056])
            {
                yield return Case($"MSTORE and MLOAD at {offset}", setup,
                    [PUSH32, .. WordA, .. Push2(offset), MSTORE, .. Push2(offset), MLOAD, PUSH1, 0, MSTORE, .. Push2((ushort)(offset - 1)), MLOAD, MSIZE, STOP]);
                yield return Case($"MLOAD at {offset} of grown memory", setup, [.. Push2(offset), MLOAD, .. Push2(offset), MLOAD, MSIZE, STOP]);
            }

            // Memory growing a word at a time, through the inline memory, past it and past the first array.
            yield return Case("MSTORE growing memory a word at a time", setup, [.. Repeated(70, MSIZE, MSIZE, MSTORE), MSIZE, STOP]);
            yield return Case("MSTORE growing memory a word at a time then loading it", setup,
                [.. Repeated(40, MSIZE, MSIZE, MSTORE), .. Enumerable.Range(0, 40).SelectMany(static k => (byte[])[.. Push2((ushort)(k * 32 + 1)), MLOAD, POP]), STOP]);

            // A store that starts inside the initialized memory extends it; one that starts past it leaves a gap to clear.
            yield return Case("MSTORE overlapping the end of the initialized memory", setup,
                [PUSH32, .. WordA, PUSH1, 0, MSTORE, PUSH32, .. WordB, PUSH1, 16, MSTORE, PUSH1, 16, MLOAD, MSIZE, STOP]);
            yield return Case("MSTORE leaving a gap", setup, [PUSH32, .. WordA, PUSH1, 0x80, MSTORE, PUSH1, 0x40, MLOAD, PUSH1, 0x60, MLOAD, MSIZE, STOP]);
            yield return Case("MSTORE8 then MLOAD across it", setup, [PUSH1, 0xa5, PUSH1, 40, MSTORE8, PUSH1, 9, MLOAD, PUSH1, 40, MLOAD, MSIZE, STOP]);

            // Offsets that only run out of gas: the largest addressable word, 2^32 and past it, and high limbs.
            yield return Case("MLOAD at 2^31 - 32", setup, [PUSH4, 0x7f, 0xff, 0xff, 0xe0, MLOAD, STOP]);
            yield return Case("MSTORE at 2^31 - 32", setup, [PUSH1, 1, PUSH4, 0x7f, 0xff, 0xff, 0xe0, MSTORE, STOP]);
            yield return Case("MLOAD at 2^32 - 32", setup, [PUSH4, 0xff, 0xff, 0xff, 0xe0, MLOAD, STOP]);
            yield return Case("MLOAD at 2^32", setup, [PUSH5, 1, 0, 0, 0, 0, MLOAD, STOP]);
            yield return Case("MSTORE at 2^32", setup, [PUSH1, 1, PUSH5, 1, 0, 0, 0, 0, MSTORE, STOP]);
            yield return Case("MLOAD with a high limb", setup, [PUSH32, 1, .. new byte[31], MLOAD, STOP]);
            yield return Case("MSTORE with a high limb", setup, [PUSH1, 1, PUSH32, 0, 0, 0, 0, 0, 0, 0, 1, .. new byte[24], MSTORE, STOP]);
            yield return Case("MLOAD of active memory with a high limb", setup, [.. twoWords, PUSH32, 0, 0, 0, 0, 0, 0, 1, .. new byte[25], MLOAD, STOP]);

            // Stack depths short of an operand.
            yield return Case("MLOAD on an empty stack", setup, [MLOAD]);
            yield return Case("MSTORE on an empty stack", setup, [MSTORE]);
            yield return Case("MSTORE with one operand", setup, [.. twoWords, PUSH1, 0, MSTORE]);
            yield return Case("MLOAD at the end of the code", setup, [.. twoWords, PUSH1, 0, MLOAD]);
        }
    }

    [TestCaseSource(nameof(Cases))]
    public void Fast_tables_match_the_plain_handlers_on_every_amount_of_gas(byte[] code, Setup setup)
    {
        Harness harness = new();
        Outcome ample = harness.Run(code, AmpleGas, Table.PlainNoTrace, setup);
        ulong gasUsed = IsFault(ample.Exception) ? 0 : AmpleGas - ample.GasLeft;
        List<string> mismatches = [];
        harness.Compare(code, AmpleGas, setup, [], mismatches);
        for (ulong gas = 0; gas <= gasUsed && mismatches.Count < 5; gas++)
            harness.Compare(code, gas, setup, [], mismatches);

        Assert.That(mismatches, Is.Empty);
    }

    private static IEnumerable<TestCaseData> FallbackCases()
    {
        byte[] twoWords = [PUSH32, .. WordA, PUSH1, 0, MSTORE, PUSH32, .. WordB, PUSH1, 32, MSTORE];
        // The two stores grow fresh memory within its initialized inline tier.
        yield return Fallbacks("Stores growing fresh memory", Setup.Fresh, twoWords, 0);
        // A reused frame initializes nothing, so the first store at 0 extends the initialized prefix without a gap.
        yield return Fallbacks("Stores growing reused memory", Setup.DirtyInline, twoWords, 0);
        yield return Fallbacks("MLOAD of the last active word", Setup.DirtyInline, [.. twoWords, PUSH1, 32, MLOAD], 0);
        yield return Fallbacks("MLOAD reaching past the active size", Setup.Fresh, [.. twoWords, PUSH1, 33, MLOAD], 1);
        yield return Fallbacks("MLOAD reaching past the initialized size", Setup.GrownDirtyArray, [.. twoWords, PUSH1, 33, MLOAD], 1);
        yield return Fallbacks("MLOAD inside the chunk the slow path zeroed", Setup.GrownDirtyArray, [.. twoWords, PUSH1, 33, MLOAD, PUSH1, 224, MLOAD], 1);
        yield return Fallbacks("MLOAD at 2^32", Setup.Fresh, [.. twoWords, PUSH5, 1, 0, 0, 0, 0, MLOAD], 1);
        yield return Fallbacks("MLOAD short of gas", Setup.Fresh, [PUSH1, 0, MLOAD], 1, gas: 5);
        yield return Fallbacks("MSTORE leaving a gap in reused memory", Setup.DirtyInline, [PUSH1, 1, PUSH1, 0x80, MSTORE], 1);
        yield return Fallbacks("MSTORE leaving a gap in fresh memory", Setup.Fresh, [PUSH1, 1, PUSH1, 0x80, MSTORE], 0);
        yield return Fallbacks("MSTORE of the last inline word", Setup.Fresh, [PUSH1, 1, PUSH2, 0x03, 0xe0, MSTORE], 0);
        yield return Fallbacks("MSTORE past the inline memory", Setup.Fresh, [PUSH1, 1, PUSH2, 0x03, 0xe1, MSTORE], 1);
        yield return Fallbacks("MSTORE growing memory a word at a time", Setup.DirtyInline, Repeated(40, MSIZE, MSIZE, MSTORE), 1);
        yield return Fallbacks("MSTORE short of expansion gas", Setup.Fresh, [PUSH1, 1, PUSH1, 0, MSTORE], 1, gas: 3 + 3 + 3 + 2);
        yield return Fallbacks("MSTORE with one operand", Setup.Fresh, [PUSH1, 0, MSTORE], 1);
    }

    /// <summary>Pins which cases the fast paths finish themselves, by counting the runs of the fallback half's handlers.</summary>
    [TestCaseSource(nameof(FallbackCases))]
    public void Fast_paths_fall_back_only_outside_their_common_case(byte[] code, Setup setup, int fallbacks, ulong gas)
    {
        Harness harness = new();
        Outcome plain = harness.Run(code, gas, Table.PlainNoTrace, setup);
        Outcome counted = harness.Run(code, gas, Table.NoTrace, setup, countFallbacks: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_fallbacks, Is.EqualTo(fallbacks));
            Assert.That(counted, Is.EqualTo(plain));
        }
    }

    /// <summary>A stale pooled array must reach memory only once it has been initialized.</summary>
    [Test]
    public void Grown_dirty_array_scenario_spills_into_the_stale_array()
    {
        Harness harness = new();
        Outcome outcome = harness.Run([PUSH2, 0x04, 0x00, MLOAD, PUSH2, 0x04, 0x20, MLOAD, STOP], AmpleGas, Table.NoTrace, Setup.GrownDirtyArray,
            inspect: static (ref EvmPooledMemory memory) =>
            {
                byte[]? backing = memory.BackingArray;
                Assert.That(backing, Is.Not.Null);
                Assert.That(backing!.Length, Is.EqualTo(Harness.DirtyArrayLength));
                Assert.That(backing[^1], Is.EqualTo(Harness.StaleByte), "the frame must spill into the stale array the setup pooled");
            });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Exception, Is.EqualTo(EvmExceptionType.Stop));
            Assert.That(outcome.Stack, Is.EqualTo(new string('0', 2 * 2 * EvmStack.WordSize)));
            Assert.That(outcome.MemorySize, Is.EqualTo(4096UL));
        }
    }

    /// <summary>Every mainnet fork, found by reflection so that a fork added later is swept too, with a seed of its own.</summary>
    internal static IEnumerable<TestCaseData> Forks()
    {
        Type[] forks = typeof(Osaka).Assembly.GetTypes()
            .Where(static t => t.Namespace == typeof(Osaka).Namespace && !t.IsAbstract && t.IsSubclassOf(typeof(NamedReleaseSpec)))
            .OrderBy(static t => t.Name, StringComparer.Ordinal)
            .ToArray();
        if (forks.Length < 2)
            throw new InvalidOperationException("The forks are no longer found by their namespace, so the sweep would check almost nothing.");

        for (int seed = 0; seed < forks.Length; seed++)
        {
            IReleaseSpec fork = (IReleaseSpec)forks[seed].GetProperty(nameof(Osaka.Instance), BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)!.GetValue(null)!;
            yield return new TestCaseData(fork, seed).SetName($"{{m}}({forks[seed].Name})");
        }
    }

    /// <summary>
    /// Random programs on every fork: a fork that changes what the plain handlers charge must change the fast paths too,
    /// or fail here.
    /// </summary>
    [TestCaseSource(nameof(Forks))]
    public void Random_programs_match_the_plain_handlers(IReleaseSpec fork, int seed)
    {
        Harness harness = new(fork);
        List<string> mismatches = [];
        for (int i = 0; i < ProgramsPerFork && mismatches.Count < 5; i++)
        {
            Random random = new(seed * 1_000_003 + i);
            byte[] code = Generate(random);
            byte[] input = new byte[random.Next(0, 90)];
            random.NextBytes(input);
            Setup setup = (Setup)random.Next(4);
            ulong gas = random.Next(5) switch
            {
                0 => (ulong)random.Next(0, 60),
                1 => (ulong)random.Next(0, 400),
                2 => (ulong)random.Next(0, 3000),
                3 => (ulong)random.Next(0, 40000),
                _ => ProgramGas,
            };
            byte[] stack = RandomStack(random);

            for (int pass = 0; pass < 4; pass++)
            {
                if (pass >= 2)
                {
                    // Gas boundaries: exactly what a plain run uses, then one short of it.
                    Outcome plain = harness.Run(code, ProgramGas, Table.PlainNoTrace, setup, input, stack);
                    if (IsFault(plain.Exception)) break;
                    ulong used = ProgramGas - plain.GasLeft;
                    gas = used - Math.Min(used, (ulong)(pass - 2));
                }
                else if (pass == 1)
                {
                    gas = gas * 2 + 21;
                }

                harness.Compare(code, gas, setup, input, mismatches, stack);
            }
        }

        Assert.That(mismatches, Is.Empty);
    }

    internal static bool IsFault(EvmExceptionType exception) => exception is not (EvmExceptionType.None or EvmExceptionType.Stop or EvmExceptionType.Revert);

    private static byte[] Generate(Random random)
    {
        List<byte> code = [];
        int snippets = random.Next(4, 60);
        for (int s = 0; s < snippets; s++)
        {
            switch (random.Next(100))
            {
                case < 14: PushOffset(code, random); code.Add(MLOAD); break;
                case < 28: PushValue(code, random); PushOffset(code, random); code.Add(MSTORE); break;
                case < 33: PushValue(code, random); PushOffset(code, random); code.Add(MSTORE8); break;
                case < 39: code.AddRange([MSIZE, MSIZE, MSTORE]); break;
                case < 43: code.AddRange([MSIZE, PUSH1, (byte)random.Next(0, 70), SWAP1, SUB, MLOAD]); break;
                case < 46:
                    PushValue(code, random);
                    code.AddRange([MSIZE, PUSH1, (byte)random.Next(0, 70), SWAP1, SUB, MSTORE]);
                    break;
                case < 49: code.Add(MSIZE); break;
                case < 53: PushLength(code, random); PushOffset(code, random); PushOffset(code, random); code.Add(MCOPY); break;
                case < 56: PushLength(code, random); PushOffset(code, random); code.Add(KECCAK256); break;
                case < 58: PushLength(code, random); PushOffset(code, random); code.Add(RETURN); break;
                case < 59: PushLength(code, random); PushOffset(code, random); code.Add(REVERT); break;
                case < 63: code.AddRange([DUP1, MLOAD]); break;
                case < 67: code.Add(MSTORE); break;
                case < 70: code.Add(MLOAD); break;
                case < 72: code.Add(MSTORE8); break;
                case < 75: code.Add(POP); break;
                case < 78: code.Add(random.Next(2) == 0 ? DUP1 : DUP2); break;
                case < 81: code.Add(SWAP1); break;
                case < 83: code.Add(ADD); break;
                case < 86: code.Add(JUMPDEST); break;
                case < 89: code.AddRange([PUSH1, (byte)random.Next(0, Math.Max(1, code.Count + 4)), random.Next(2) == 0 ? JUMP : JUMPI]); break;
                case < 91: code.Add(GAS); break;
                case < 93: code.AddRange([PUSH1, (byte)random.Next(0, 100), CALLDATALOAD]); break;
                case < 96: code.Add(PUSH0); break;
                case < 99: PushValue(code, random); break;
                default: code.Add(STOP); break;
            }
        }

        return code.Count == 0 ? [STOP] : code.ToArray();
    }

    /// <summary>Pushes a memory offset on a boundary a fast path tests, or one of the offsets that only run out of gas.</summary>
    private static void PushOffset(List<byte> code, Random random)
    {
        switch (random.Next(14))
        {
            case 0: code.AddRange([PUSH1, (byte)random.Next(0, 70)]); break;
            case 1: code.AddRange([PUSH1, (byte)(random.Next(0, 8) * 32)]); break;
            case 2: code.AddRange(Push2((ushort)random.Next(950, 1100))); break;
            case 3: code.AddRange(Push2((ushort)random.Next(2000, 2100))); break;
            case 4: code.AddRange(Push2((ushort)random.Next(4040, 4140))); break;
            case 5: code.AddRange(Push2((ushort)random.Next(0, 8192))); break;
            case 6: code.AddRange([PUSH4, 0x7f, 0xff, 0xff, (byte)random.Next(0xc0, 0x100)]); break;
            case 7: code.AddRange([PUSH4, 0xff, 0xff, 0xff, (byte)random.Next(0xc0, 0x100)]); break;
            case 8: code.AddRange([PUSH5, 1, 0, 0, 0, (byte)random.Next(0, 64)]); break;
            case 9:
                {
                    byte[] word = new byte[32];
                    word[random.Next(0, 24)] = (byte)random.Next(1, 256);
                    word[31] = (byte)random.Next(0, 64);
                    code.Add(PUSH32);
                    code.AddRange(word);
                    break;
                }
            case 10: code.Add(MSIZE); break;
            case 11: code.AddRange([MSIZE, PUSH1, 32, SWAP1, SUB]); break;
            default: code.AddRange([PUSH1, (byte)random.Next(0, 256)]); break;
        }
    }

    private static void PushValue(List<byte> code, Random random)
    {
        switch (random.Next(4))
        {
            case 0: code.AddRange([PUSH1, (byte)random.Next(0, 256)]); break;
            case 1: code.Add(PUSH0); break;
            default:
                code.Add(PUSH32);
                for (int b = 0; b < 32; b++) code.Add((byte)random.Next(256));
                break;
        }
    }

    private static void PushLength(List<byte> code, Random random)
    {
        switch (random.Next(4))
        {
            case 0: code.Add(PUSH0); break;
            case 1: code.AddRange(Push2((ushort)random.Next(0, 2100))); break;
            default: code.AddRange([PUSH1, (byte)random.Next(0, 100)]); break;
        }
    }

    /// <summary>A few words for the programs to start on: small offsets, zero, and words with high limbs.</summary>
    private static byte[] RandomStack(Random random)
    {
        byte[] stack = new byte[random.Next(0, 6) * EvmStack.WordSize];
        for (int at = 0; at < stack.Length; at += EvmStack.WordSize)
        {
            switch (random.Next(4))
            {
                case 0: break;
                case 1: stack[at + 24] = 1; break;
                default:
                    stack[at] = (byte)random.Next(256);
                    stack[at + 1] = (byte)random.Next(0, 9);
                    break;
            }
        }

        return stack;
    }

    private static TestCaseData Case(string name, Setup setup, byte[] code) =>
        new TestCaseData(code, setup).SetName($"{{m}}({name}, {setup})");

    private static TestCaseData Fallbacks(string name, Setup setup, byte[] code, int fallbacks, ulong gas = AmpleGas) =>
        new TestCaseData(code, setup, fallbacks, gas).SetName($"{{m}}({name})");

    private static byte[] Push2(ushort value) => [PUSH2, (byte)(value >> 8), (byte)value];

    private static byte[] Repeated(int times, params byte[] ops) => Enumerable.Repeat(ops, times).SelectMany(static o => o).ToArray();

    private static unsafe EvmExceptionType CountingFallback(
        ref EvmStack stack, ref EthereumGasPolicy gas, ref DispatchState state, nint pc, nint opCodeCount)
    {
        _fallbacks++;
        return ((delegate*<ref EvmStack, ref EthereumGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>)_plainHandlers![Unsafe.Add(ref stack.Code, pc)])(
            ref stack, ref gas, ref state, pc, opCodeCount);
    }

    /// <summary>Runs code through the dispatch tables of one virtual machine and one aligned stack.</summary>
    internal sealed unsafe class Harness
    {
        public const int DirtyArrayLength = 8192;
        public const byte StaleByte = 0xa5;
        private const int CancellationPollInterval = 1024;

        private readonly DispatchingVirtualMachine _vm;
        private readonly byte[] _stackBytes = GC.AllocateArray<byte>((EvmStack.MaxStackSize + 1) * EvmStack.WordSize, pinned: true);
        private readonly int _stackStart;

        public Harness(IReleaseSpec? spec = null)
        {
            _vm = new DispatchingVirtualMachine(spec ?? ReleaseSpec);
            int alignment = (int)((nuint)Unsafe.AsPointer(ref _stackBytes[0]) & (EvmStack.WordSize - 1));
            _stackStart = alignment == 0 ? 0 : EvmStack.WordSize - alignment;
        }

        /// <summary>How many times the last run that counted fallbacks entered the table's plain half.</summary>
        public int Fallbacks => _fallbacks;

        /// <summary>How many of the last run's opcodes were counted on the machine rather than by the chain.</summary>
        /// <remarks>An untraced PUSH2 that runs the jump after it counts that jump, and the JUMPDEST it lands on, there.</remarks>
        public int MachineOpCodeCount { get; private set; }

        /// <summary>Runs <paramref name="code"/> through every table and records how each differs from the plain untraced one.</summary>
        public void Compare(byte[] code, ulong gas, Setup setup, byte[] input, List<string> mismatches, byte[]? stack = null)
        {
            Outcome plain = Run(code, gas, Table.PlainNoTrace, setup, input, stack);
            foreach (Table table in Tables)
            {
                if (table == Table.PlainNoTrace) continue;
                Outcome outcome = Run(code, gas, table, setup, input, stack);
                // Untraced dispatch leaves out a push that ends the code, and a checked body that faults reports its program
                // counter one short; nothing can read either.
                if (table == Table.Traced && (outcome.Pc >= code.Length || IsFault(outcome.Exception)))
                    outcome = outcome with { Pc = IsFault(outcome.Exception) || plain.Pc >= code.Length ? plain.Pc : outcome.Pc, Head = plain.Head, Stack = plain.Stack };

                if (outcome != plain)
                    mismatches.Add($"{table} gas {gas} {setup} code {Convert.ToHexString(code)} input {Convert.ToHexString(input)} stack {Convert.ToHexString(stack ?? [])}\n {table,-22} {outcome}\n {"plain",-22} {plain}");
            }
        }

        public Outcome Run(byte[] code, ulong gas, Table table, Setup setup, byte[]? input = null, byte[]? stack = null,
            bool countFallbacks = false, EvmPooledMemoryInspector? inspect = null)
        {
            input ??= [];
            stack ??= [];
            CodeInfo codeInfo = new(code);
            using ExecutionEnvironment env = ExecutionEnvironment.Rent(codeInfo, Address.Zero, Address.Zero, null, 0, UInt256.Zero, input);
            using StackAccessTracker accessTracker = new();
            using VmState<EthereumGasPolicy> frame = VmState<EthereumGasPolicy>.RentTopLevel(
                EthereumGasPolicy.FromULong(gas), ExecutionType.TRANSACTION, env, accessTracker, default);
            frame.Memory = CreateMemory(setup);
            _vm.Enter(frame);

            Array.Clear(_stackBytes);
            stack.CopyTo(_stackBytes, _stackStart);
            int head = stack.Length / EvmStack.WordSize;

            delegate*<ref EvmStack, ref EthereumGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>[] handlers = table switch
            {
                Table.NoTrace or Table.PlainNoTrace => _vm.GetOpcodeHandlers<OffFlag, OffFlag>(),
                Table.NoTraceCancelable or Table.PlainNoTraceCancelable => _vm.GetOpcodeHandlers<OffFlag, OnFlag>(),
                _ => _vm.GetOpcodeHandlers<OnFlag, OffFlag>(),
            };
            if (countFallbacks)
            {
                Assert.That(handlers.Length, Is.EqualTo(2 * VirtualMachine<EthereumGasPolicy>.FallbackHandlersOffset), "an untraced table carries its fallback half");
                handlers = (delegate*<ref EvmStack, ref EthereumGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>[])handlers.Clone();
                _plainHandlers = new nint[VirtualMachine<EthereumGasPolicy>.FallbackHandlersOffset];
                for (int opcode = 0; opcode < _plainHandlers.Length; opcode++)
                {
                    _plainHandlers[opcode] = (nint)handlers[VirtualMachine<EthereumGasPolicy>.FallbackHandlersOffset + opcode];
                    handlers[VirtualMachine<EthereumGasPolicy>.FallbackHandlersOffset + opcode] = &CountingFallback;
                }
                _fallbacks = 0;
            }

            bool cancelable = table is Table.NoTraceCancelable or Table.PlainNoTraceCancelable;
            _vm.OpCodeCount = 0;
            EthereumGasPolicy gasPolicy = EthereumGasPolicy.FromULong(gas);
            EvmExceptionType exception;
            nint pc;
            nint finalHead;
            nint opCodeCount;
            fixed (delegate*<ref EvmStack, ref EthereumGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>* entries = handlers)
            {
                delegate*<ref EvmStack, ref EthereumGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>* dispatch =
                    table is Table.PlainNoTrace or Table.PlainNoTraceCancelable ? entries + VirtualMachine<EthereumGasPolicy>.FallbackHandlersOffset : entries;

                EvmStack evmStack = new(head, _vm.Tracer, ref _stackBytes[_stackStart], codeInfo.ExecutionCodeSpan, codeInfo);
                evmStack.HoistInputData(input);
                DispatchState state = new() { OpcodeHandlers = dispatch, Vm = _vm, CancellationPollAt = CancellationPollInterval };
                pc = 0;
                opCodeCount = 0;
                while (true)
                {
                    exception = dispatch[code[pc]](ref evmStack, ref gasPolicy, ref state, pc, opCodeCount);
                    // The cancelable loop's re-entry after a poll, as RunDispatchLoop makes it.
                    if (!cancelable || exception != EvmExceptionType.None || state.OpCodeCount < state.CancellationPollAt ||
                        (nuint)state.FinalProgramCounter >= (nuint)code.Length)
                        break;
                    pc = state.FinalProgramCounter;
                    opCodeCount = state.OpCodeCount;
                    state.CancellationPollAt = opCodeCount + CancellationPollInterval;
                }

                pc = state.FinalProgramCounter;
                MachineOpCodeCount = _vm.OpCodeCount;
                opCodeCount = state.OpCodeCount + MachineOpCodeCount;
                finalHead = evmStack.Head;
            }

            // Untraced dispatch runs off the end into the code's STOP padding, which RunByteCode reads as the implicit
            // STOP at the end of the code.
            if (table != Table.Traced && exception == EvmExceptionType.Stop && pc > code.Length)
            {
                exception = EvmExceptionType.None;
                pc = code.Length;
                opCodeCount--;
            }

            _vm.ReturnData = null;
            inspect?.Invoke(ref frame.Memory);
            string stackHex = Convert.ToHexString(_stackBytes, _stackStart, (int)Math.Clamp(finalHead, 0, EvmStack.MaxStackSize) * EvmStack.WordSize);
            ulong size = frame.Memory.Size;
            string memoryHex = "";
            if (!IsFault(exception))
            {
                Assert.That(frame.Memory.TryLoadSpan(UInt256.Zero, size, out Span<byte> memory), Is.True);
                memoryHex = Convert.ToHexString(memory);
            }

            return new Outcome(exception, gasPolicy.Value, pc, finalHead, stackHex, memoryHex, size, opCodeCount);
        }

        private static EvmPooledMemory CreateMemory(Setup setup)
        {
            if (setup == Setup.Fresh)
                return new EvmPooledMemory(new EvmFrameMemory(), isFresh: true);

            EvmFrameMemory frameMemory = new();
            EvmPooledMemory stale = new(frameMemory, isFresh: true);
            Assert.That(stale.TrySave(UInt256.Zero, Filled(EvmPooledMemory.InlineCapacity, StaleByte)), Is.True);
            if (setup >= Setup.DirtyArray)
                PoolStaleArray();

            EvmPooledMemory memory = new(frameMemory);
            if (setup == Setup.GrownDirtyArray)
                _ = memory.CalculateMemoryCost(UInt256.Zero, 4096UL, out _);
            return memory;
        }

        /// <summary>Leaves the thread's memory cache holding one array, full of stale bytes, for the next spill to rent.</summary>
        private static void PoolStaleArray()
        {
            ThreadCacheField.SetValue(null, Activator.CreateInstance(ThreadCacheField.FieldType));
            EvmPooledMemory pooled = new(new EvmFrameMemory());
            Assert.That(pooled.TrySave(UInt256.Zero, Filled(DirtyArrayLength, StaleByte)), Is.True);
            Assert.That(pooled.BackingArray, Has.Length.EqualTo(DirtyArrayLength));
            pooled.Dispose();
        }

        private static byte[] Filled(int length, byte value) => Enumerable.Repeat(value, length).ToArray();
    }

    internal delegate void EvmPooledMemoryInspector(ref EvmPooledMemory memory);

    /// <summary>A virtual machine over a block of one fork, whose current frame a test can set.</summary>
    private sealed class DispatchingVirtualMachine : VirtualMachine<EthereumGasPolicy>
    {
        public DispatchingVirtualMachine(IReleaseSpec spec)
            : base(new NoBlockhashProvider(), new SingleReleaseSpecProvider(spec, BlockchainIds.Mainnet, BlockchainIds.Mainnet), LimboLogs.Instance)
        {
            SetBlockExecutionContext(new BlockExecutionContext(Header, spec));
            _txTracer = new SilentTracer();
        }

        /// <summary>The tracer the traced table reports to, which records nothing.</summary>
        public ITxTracer Tracer => _txTracer;

        public void Enter(VmState<EthereumGasPolicy> frame) => VmState = frame;
    }

    private sealed class SilentTracer : TxTracer;

    private sealed class NoBlockhashProvider : IBlockhashProvider
    {
        public Hash256? GetBlockhash(BlockHeader currentBlock, ulong number, IReleaseSpec spec) => null;

        public System.Threading.Tasks.Task Prefetch(BlockHeader currentBlock, System.Threading.CancellationToken token) =>
            System.Threading.Tasks.Task.CompletedTask;
    }
}
