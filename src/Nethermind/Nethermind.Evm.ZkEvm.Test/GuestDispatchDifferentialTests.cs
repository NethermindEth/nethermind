// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.ZkEvm.Test;

/// <summary>Runs programs through the guest's untraced table, and through its cancelable and traced ones for reference.</summary>
/// <remarks>
/// Untraced dispatch carries the remaining gas and the stack head in registers, pays checked bodies' charges and tests
/// their stack bounds itself, skips POP's body, and runs the guest handlers, which fuse common opcode sequences. The
/// cancelable table dispatches the same way through the shared handlers only, and the traced one hands the unchecked
/// bodies the frame's stack and gas policy, so they do their own charges and tests. All must reach the same outcome -
/// fault, gas, program counter, stack and memory - on every input, so the programs lean on the shapes the guest handlers
/// fuse and on the limits dispatch tests: gas that runs out inside a fused step, stacks at the limit, destinations the
/// bitmap does or does not hold yet, and immediates cut short by the end of the code. Opcodes that need a world state,
/// and KECCAK256, which a ZK_EVM test process cannot hash, run as INVALID in every table.
/// </remarks>
public class GuestDispatchDifferentialTests
{
    private const int ProgramsPerSeed = 1500;

    private static readonly IReleaseSpec ReleaseSpec = Osaka.Instance;

    /// <summary>Forks either side of the gates on the opcodes the guest handlers cover, and the next one.</summary>
    /// <remarks>The guest installs its handlers on every spec, so a fork that gates or reprices one of them has to be here.</remarks>
    private static readonly IReleaseSpec[] Forks = [Byzantium.Instance, Constantinople.Instance, Shanghai.Instance, ReleaseSpec, Amsterdam.Instance];
    private static readonly IReleaseSpec[] StorageLoadForks = [ReleaseSpec, Amsterdam.Instance];
    private static readonly BlockHeader Header = new(Hash256.Zero, Hash256.Zero, Address.Zero, UInt256.Zero, 1, 30_000_000, 1, []);

    public enum Table { Untraced, Cancelable, Traced }

    private readonly record struct Outcome(
        EvmExceptionType Exception, ulong GasLeft, nint Pc, nint Head, string Stack, string Memory, ulong MemorySize, EthereumGasPolicy Policy);

    [Test]
    public void Random_programs_match_the_shared_handlers([ValueSource(nameof(Forks))] IReleaseSpec spec, [Range(0, 7)] int seed)
    {
        List<string> mismatches = [];
        for (int i = 0; i < ProgramsPerSeed && mismatches.Count < 5; i++)
        {
            Random random = new(seed * 1_000_003 + i);
            byte[] code = Generate(random);
            byte[] input = new byte[random.Next(0, 90)];
            random.NextBytes(input);
            ulong gas = (ulong)(random.Next(4) switch { 0 => random.Next(0, 60), 1 => random.Next(0, 400), 2 => random.Next(0, 3000), _ => random.Next(0, 40000) });
            int head = random.Next(10) switch { 0 => 1024, 1 => 1023, 2 => 1022, 3 => 1021, 4 => random.Next(0, 3), _ => random.Next(6, 24) };

            // One code info per table across the passes, so later passes see the bitmap the earlier ones left.
            CodeInfo untracedInfo = new(code);
            CodeInfo cancelableInfo = new(code);
            CodeInfo tracedInfo = new(code);
            for (int pass = 0; pass < 4; pass++)
            {
                if (pass >= 2)
                {
                    // Gas boundaries: exactly what a fresh run uses, then one short of it.
                    Outcome fresh = Run(gas, input, head, new CodeInfo(code), Table.Traced, spec);
                    if (IsFault(fresh.Exception)) break;
                    ulong used = gas - fresh.GasLeft;
                    gas = used - Math.Min(used, (ulong)(pass - 2));
                }

                Outcome untraced = Run(gas, input, head, untracedInfo, Table.Untraced, spec);
                Outcome cancelable = Run(gas, input, head, cancelableInfo, Table.Cancelable, spec);
                Outcome traced = Run(gas, input, head, tracedInfo, Table.Traced, spec);
                if (!Matches(untraced, cancelable) || !Matches(untraced, traced))
                    mismatches.Add($"program {i} pass {pass} gas {gas} head {head} code {Convert.ToHexString(code)} input {Convert.ToHexString(input)}\n untraced   {untraced}\n cancelable {cancelable}\n traced     {traced}");
            }
        }

        Assert.That(mismatches, Is.Empty);
    }

    [Test]
    public void Gas_observes_the_charges_of_preceding_opcodes([Values] Table table)
    {
        // Checked, fixed-cost and full-policy (MSTORE) bodies charge gas that dispatch carries by value between handlers,
        // and the execution gas is all they may change in the frame's policy.
        byte[] code =
        [
            (byte)Instruction.PUSH1, 1, (byte)Instruction.PUSH1, 2, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 11, (byte)Instruction.JUMPI, (byte)Instruction.INVALID, (byte)Instruction.INVALID, (byte)Instruction.INVALID,
            (byte)Instruction.JUMPDEST, (byte)Instruction.PUSH2, 0, 16, (byte)Instruction.JUMP,
            (byte)Instruction.JUMPDEST, (byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.MSTORE, (byte)Instruction.GAS
        ];
        const ulong gas = 100_000;
        const ulong charged = 6 * GasCostOf.VeryLow + GasCostOf.High + GasCostOf.Mid + 2 * GasCostOf.JumpDest + 3 * GasCostOf.Base + GasCostOf.Memory;
        EthereumGasPolicy initial = EthereumGasPolicy.FromULong(gas) with
        {
            StateReservoir = 13,
            StateGasUsed = 23,
            StateGasSpill = 31,
            StateGasSpillRefunded = 7,
            IndependentStatePool = true,
        };

        Outcome outcome = Run(gas, [], 0, new CodeInfo(code), table, initial: initial);
        byte[] pushLeft = [(byte)Instruction.PUSH8, .. ((UInt256)(gas - charged)).ToBigEndian()[^8..]];
        Outcome pushed = Run(gas, [], 0, new CodeInfo(pushLeft), table);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Exception, Is.EqualTo(EvmExceptionType.Stop));
            Assert.That(outcome.Policy, Is.EqualTo(initial with { Value = gas - charged }));
            Assert.That(outcome.Head, Is.EqualTo((nint)1));
            Assert.That(outcome.Stack, Is.EqualTo(pushed.Stack));
        }
    }

    /// <remarks>POP, CALLDATALOAD and CLZ run checked in every table, so the random programs cannot cover them.</remarks>
    [Test]
    public void Always_checked_bodies_keep_the_carried_head([Values] Table table)
    {
        byte[] input = [0x00, 0x0f, .. new byte[30]];
        byte[] code = [(byte)Instruction.PUSH1, 0, (byte)Instruction.CALLDATALOAD, (byte)Instruction.CLZ, (byte)Instruction.PUSH0, (byte)Instruction.POP];
        const ulong gas = 100;
        const ulong charged = 2 * GasCostOf.VeryLow + GasCostOf.Low + 2 * GasCostOf.Base;

        byte[] pushLeadingZeros = [(byte)Instruction.PUSH1, 12];

        Outcome outcome = Run(gas, input, 0, new CodeInfo(code), table);
        Outcome pushed = Run(gas, [], 0, new CodeInfo(pushLeadingZeros), table);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Exception, Is.EqualTo(EvmExceptionType.Stop));
            Assert.That(outcome.GasLeft, Is.EqualTo(gas - charged));
            Assert.That(outcome.Head, Is.EqualTo((nint)1));
            Assert.That(outcome.Stack, Is.EqualTo(pushed.Stack));
        }
    }

    /// <remarks>
    /// The operands lean on the limb and sign boundaries the guest's arithmetic splits its cases on; each result is
    /// stored to memory, and the gas each program leaves tells a charge that differs.
    /// </remarks>
    [Test]
    public void Arithmetic_matches_the_shared_handlers(
        [Values(Instruction.MUL, Instruction.DIV, Instruction.SDIV, Instruction.MOD, Instruction.SMOD, Instruction.ADDMOD, Instruction.MULMOD,
            Instruction.SIGNEXTEND, Instruction.NOT, Instruction.BYTE, Instruction.SHL, Instruction.SHR, Instruction.SAR)] Instruction op,
        [Range(0, 3)] int seed)
    {
        List<string> mismatches = [];
        Random random = new(seed * 7919 + (int)op);
        for (int i = 0; i < 400 && mismatches.Count < 5; i++)
        {
            List<byte> code = [];
            for (int operand = 0; operand < 3; operand++)
                code.AddRange([(byte)Instruction.PUSH32, .. EdgeWord(random)]);
            code.AddRange([(byte)op, (byte)Instruction.PUSH1, 0, (byte)Instruction.MSTORE, (byte)Instruction.STOP]);
            byte[] program = [.. code];

            Outcome untraced = Run(100_000, [], 0, new CodeInfo(program), Table.Untraced);
            Outcome traced = Run(100_000, [], 0, new CodeInfo(program), Table.Traced);
            if (!Matches(untraced, traced))
                mismatches.Add($"code {Convert.ToHexString(program)}\n untraced {untraced}\n traced   {traced}");
        }

        Assert.That(mismatches, Is.Empty);
    }

    /// <remarks>
    /// The second load of a key finds it warm, and every amount of gas up to the program's need puts the end of the
    /// gas inside each cold and warm charge, up to what the traced table charges for the loads on each fork. The guest
    /// builds one table for the first fork it prepares, so on EIP-8038 this checks that whichever SLOAD handler the
    /// table holds charges that fork's costs, not that the guest one stays out.
    /// </remarks>
    [Test]
    public void Storage_loads_match_the_shared_handlers([ValueSource(nameof(StorageLoadForks))] IReleaseSpec spec, [Values] bool fromEmptyStack)
    {
        byte[] loads =
        [
            (byte)Instruction.PUSH1, 5, (byte)Instruction.SLOAD, (byte)Instruction.PUSH1, 6, (byte)Instruction.SLOAD,
            (byte)Instruction.PUSH1, 5, (byte)Instruction.SLOAD, (byte)Instruction.ADD, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.MSTORE, (byte)Instruction.STOP
        ];
        byte[] code = fromEmptyStack ? [(byte)Instruction.SLOAD] : loads;
        const ulong ample = 100_000;
        ulong needed = ample - Run(ample, [], 0, new CodeInfo(loads), Table.Traced, spec, loadsStorage: true).GasLeft;
        UInt256 expected = StorageValueAt(5) * 2 + StorageValueAt(6);

        List<string> mismatches = [];
        for (ulong gas = 0; gas <= needed && mismatches.Count < 5; gas++)
        {
            Outcome untraced = Run(gas, [], 0, new CodeInfo(code), Table.Untraced, spec, loadsStorage: true);
            Outcome traced = Run(gas, [], 0, new CodeInfo(code), Table.Traced, spec, loadsStorage: true);
            if (!Matches(untraced, traced))
                mismatches.Add($"gas {gas}\n untraced {untraced}\n traced   {traced}");
        }

        Outcome complete = Run(needed, [], 0, new CodeInfo(code), Table.Untraced, spec, loadsStorage: true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mismatches, Is.Empty);
            if (fromEmptyStack)
            {
                Assert.That(complete.Exception, Is.EqualTo(EvmExceptionType.StackUnderflow));
            }
            else
            {
                Assert.That(complete.Exception, Is.EqualTo(EvmExceptionType.Stop));
                Assert.That(complete.GasLeft, Is.Zero);
                Assert.That(complete.Memory, Is.EqualTo(Convert.ToHexString(expected.ToBigEndian())));
            }
        }
    }

    /// <remarks>EIP-8038 SLOAD gas equals EIP-2929's, so only the table itself shows that the guest handler stays out.</remarks>
    [Test]
    public void Guest_storage_load_handler_is_installed_until_eip8038([ValueSource(nameof(StorageLoadForks))] IReleaseSpec spec) =>
        Assert.That(VirtualMachine<EthereumGasPolicy>.LoadsStorageThroughGuestHandlerForTests(spec), Is.EqualTo(!spec.IsEip8038Enabled));

    /// <remarks>
    /// Every amount of gas up to the program's need puts the end of the gas inside each charge. In a static frame the
    /// TSTORE faults instead.
    /// </remarks>
    [Test]
    public void Transient_storage_matches_the_shared_handlers([Values] bool isStatic)
    {
        byte[] code =
        [
            (byte)Instruction.PUSH1, 9, (byte)Instruction.PUSH1, 4, (byte)Instruction.TSTORE,
            (byte)Instruction.PUSH1, 4, (byte)Instruction.TLOAD, (byte)Instruction.PUSH1, 5, (byte)Instruction.TLOAD, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.MSTORE, (byte)Instruction.STOP
        ];
        const ulong needed = 3 + 3 + 100 + 3 + 100 + 3 + 100 + 3 + 3 + 3 + 3;

        List<string> mismatches = [];
        for (ulong gas = 0; gas <= needed && mismatches.Count < 5; gas++)
        {
            Outcome untraced = Run(gas, [], 0, new CodeInfo(code), Table.Untraced, loadsStorage: true, isStatic: isStatic);
            Outcome traced = Run(gas, [], 0, new CodeInfo(code), Table.Traced, loadsStorage: true, isStatic: isStatic);
            if (!Matches(untraced, traced))
                mismatches.Add($"gas {gas}\n untraced {untraced}\n traced   {traced}");
        }

        Outcome complete = Run(needed, [], 0, new CodeInfo(code), Table.Untraced, loadsStorage: true, isStatic: isStatic);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mismatches, Is.Empty);
            if (isStatic)
            {
                Assert.That(complete.Exception, Is.EqualTo(EvmExceptionType.StaticCallViolation));
            }
            else
            {
                Assert.That(complete.Exception, Is.EqualTo(EvmExceptionType.Stop));
                Assert.That(complete.GasLeft, Is.Zero);
                Assert.That(complete.Memory, Is.EqualTo(Convert.ToHexString(((UInt256)9).ToBigEndian())));
            }
        }
    }

    /// <remarks>
    /// Memory starts empty, holds one word at 0, or holds words up to 0x120, so a copy lands inside the initialized
    /// memory, past it with a gap, or at the end of the inline backing. Each copy also runs one and three gas short of
    /// its need, which puts the end of the gas inside its word and expansion charges.
    /// </remarks>
    [Test]
    public void Data_copies_match_the_shared_handlers([Values(Instruction.CALLDATACOPY, Instruction.RETURNDATACOPY)] Instruction op, [Values(0, 1, 2)] int memory)
    {
        byte[] data = new byte[40];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i + 1);
        byte[] prefix = memory switch
        {
            0 => [],
            1 => [(byte)Instruction.PUSH1, 0xaa, (byte)Instruction.PUSH1, 0, (byte)Instruction.MSTORE],
            _ => [(byte)Instruction.PUSH1, 0xbb, (byte)Instruction.PUSH2, 0x01, 0x00, (byte)Instruction.MSTORE],
        };
        UInt256[] destinations = [0, 7, 0x20, 0x40, 0x61, 0x120, 0x3f0, 0x400, UInt256.One << 32, UInt256.One << 64];
        UInt256[] sources = [0, 5, 39, 40, 41, UInt256.One << 32, UInt256.One << 64];
        UInt256[] sizes = [0, 1, 31, 32, 33, 64, 200, UInt256.One << 32];

        List<string> mismatches = [];
        foreach (UInt256 destination in destinations)
            foreach (UInt256 source in sources)
                foreach (UInt256 size in sizes)
                {
                    byte[] code =
                    [
                        .. prefix, (byte)Instruction.PUSH32, .. size.ToBigEndian(), (byte)Instruction.PUSH32, .. source.ToBigEndian(),
                        (byte)Instruction.PUSH32, .. destination.ToBigEndian(), (byte)op, (byte)Instruction.MSIZE, (byte)Instruction.STOP
                    ];
                    Outcome fresh = Run(100_000, data, 0, new CodeInfo(code), Table.Traced, returnData: data);
                    ulong needed = 100_000 - fresh.GasLeft;
                    foreach (ulong gas in (ulong[])[100_000, needed, needed - 1, needed - 3])
                    {
                        Outcome untraced = Run(gas, data, 0, new CodeInfo(code), Table.Untraced, returnData: data);
                        Outcome traced = Run(gas, data, 0, new CodeInfo(code), Table.Traced, returnData: data);
                        if (!Matches(untraced, traced) && mismatches.Count < 5)
                            mismatches.Add($"gas {gas} code {Convert.ToHexString(code)}\n untraced {untraced}\n traced   {traced}");
                    }
                }

        Assert.That(mismatches, Is.Empty);
    }

    /// <summary>A big-endian word drawn mostly from the values arithmetic splits its cases on.</summary>
    private static byte[] EdgeWord(Random random)
    {
        UInt256 word = random.Next(12) switch
        {
            0 => UInt256.Zero,
            1 => UInt256.One,
            2 => UInt256.MaxValue,
            3 => UInt256.One << 255,
            4 => (UInt256.One << 255) - 1,
            5 => UInt256.One << random.Next(256),
            6 => (UInt256.One << random.Next(1, 256)) - 1,
            7 => UInt256.MaxValue - (ulong)random.Next(0, 300),
            8 => (ulong)random.Next(0, 300),
            _ => RandomWord(random) >> (64 * random.Next(4)),
        };
        return word.ToBigEndian();
    }

    private static UInt256 RandomWord(Random random)
    {
        Span<byte> bytes = stackalloc byte[32];
        random.NextBytes(bytes);
        return new UInt256(bytes, isBigEndian: true);
    }

    private static bool IsFault(EvmExceptionType exception) => exception is not (EvmExceptionType.None or EvmExceptionType.Stop);

    private static bool Matches(Outcome outcome, Outcome reference) =>
        outcome.Exception == reference.Exception && (IsFault(outcome.Exception) || outcome == reference);

    private static byte[] Generate(Random random)
    {
        List<byte> code = [];
        List<int> destinationImmediates = [];
        if (random.Next(6) == 0)
        {
            // Pushes the destinations far enough out for a look-back to decide them.
            int filler = random.Next(30, 80);
            for (int f = 0; f < filler; f++) code.Add(random.Next(4) == 0 ? (byte)random.Next(0x60, 0x80) : (byte)random.Next(0x00, 0x20));
        }

        int snippets = random.Next(10, 90);
        for (int s = 0; s < snippets; s++)
        {
            int pick = random.Next(105);
            switch (pick)
            {
                case < 8: code.Add((byte)Instruction.JUMPDEST); break;
                case < 22: code.AddRange([(byte)Instruction.PUSH1, SmallImmediate(random)]); break;
                case < 26:
                    destinationImmediates.Add(code.Count + 1);
                    code.AddRange([(byte)Instruction.PUSH2, 0, 0]);
                    break;
                case < 28: code.Add((byte)Instruction.JUMP); break;
                case < 30: code.Add((byte)Instruction.JUMPI); break;
                case < 34: code.Add((byte)Instruction.DUP1); break;
                case < 37: code.Add((byte)Instruction.DUP2); break;
                case < 40: code.Add((byte)Instruction.SWAP1); break;
                case < 42: code.Add((byte)Instruction.POP); break;
                case < 45: code.Add((byte)Instruction.ISZERO); break;
                case < 48: code.Add((byte)Instruction.EQ); break;
                case < 50: code.Add((byte)Instruction.LT); break;
                case < 52: code.Add((byte)Instruction.GT); break;
                case < 55: code.Add((byte)Instruction.SHL); break;
                case < 58: code.Add((byte)Instruction.SHR); break;
                case < 61: code.Add((byte)Instruction.MSTORE); break;
                case < 64: code.Add((byte)Instruction.MLOAD); break;
                case < 66: code.Add((byte)Instruction.CALLDATALOAD); break;
                case < 67: code.AddRange([(byte)Instruction.PUSH4, (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)]); break;
                case < 68:
                    code.Add((byte)Instruction.PUSH32);
                    for (int b = 0; b < 32; b++) code.Add(random.Next(3) == 0 ? (byte)random.Next(256) : (byte)0);
                    break;
                case < 70: code.Add((byte)Instruction.PUSH0); break;
                case < 71: code.Add((byte)Instruction.ADD); break;
                case < 72: code.Add((byte)Instruction.SUB); break;
                case < 73: code.Add((byte)Instruction.MSIZE); break;
                // A bare push, which swallows whatever follows it.
                case < 74: code.Add((byte)random.Next((int)Instruction.PUSH1, (int)Instruction.PUSH32 + 1)); break;
                case < 80:
                    {
                        // A selector dispatch, sometimes matching a selector pushed just before it.
                        byte high = (byte)random.Next(4), low = (byte)random.Next(256);
                        if (random.Next(2) == 0) code.AddRange([(byte)Instruction.PUSH4, 0, 0, high, low]);
                        code.AddRange([(byte)Instruction.DUP1, (byte)Instruction.PUSH4, 0, 0, high, low, (byte)Instruction.EQ]);
                        destinationImmediates.Add(code.Count + 1);
                        code.AddRange([(byte)Instruction.PUSH2, 0, 0, (byte)Instruction.JUMPI]);
                        break;
                    }
                case < 88:
                    {
                        // A comparison and the branch on it, sometimes inverted.
                        code.Add(random.Next(6) switch
                        {
                            0 => (byte)Instruction.LT,
                            1 => (byte)Instruction.GT,
                            2 => (byte)Instruction.SLT,
                            3 => (byte)Instruction.SGT,
                            4 => (byte)Instruction.EQ,
                            _ => (byte)Instruction.ISZERO
                        });
                        if (random.Next(2) == 0) code.Add((byte)Instruction.ISZERO);
                        destinationImmediates.Add(code.Count + 1);
                        code.AddRange([(byte)Instruction.PUSH2, 0, 0, (byte)Instruction.JUMPI]);
                        break;
                    }
                case < 92:
                    destinationImmediates.Add(code.Count + 1);
                    code.AddRange([(byte)Instruction.PUSH2, 0, 0, random.Next(2) == 0 ? (byte)Instruction.JUMP : (byte)Instruction.JUMPI]);
                    break;
                case < 94: code.AddRange([(byte)Instruction.PUSH2, (byte)random.Next(0, 5), (byte)random.Next(256), (byte)Instruction.MSTORE]); break;
                case < 96: code.AddRange([(byte)Instruction.PUSH2, (byte)random.Next(0, 5), (byte)random.Next(256), (byte)Instruction.MLOAD]); break;
                case < 98: code.AddRange([(byte)Instruction.PUSH1, (byte)random.Next(0, 100), (byte)Instruction.CALLDATALOAD]); break;
                case < 99: code.Add((byte)Instruction.STOP); break;
                case < 100: code.Add((byte)Instruction.MUL); break;
                case < 101: code.Add((byte)Instruction.DIV); break;
                case < 102: code.Add((byte)Instruction.SLT); break;
                case < 103: code.Add((byte)Instruction.SGT); break;
                case < 104: code.Add((byte)Instruction.CALLDATASIZE); break;
                default: code.Add((byte)random.Next(256)); break;
            }
        }

        // Mostly JUMPDEST bytes, in PUSH data or not, and sometimes any position at all.
        List<int> jumpDestinations = [];
        for (int position = 0; position < code.Count; position++)
            if (code[position] == (byte)Instruction.JUMPDEST) jumpDestinations.Add(position);

        foreach (int immediate in destinationImmediates)
        {
            int destination = jumpDestinations.Count > 0 && random.Next(5) != 0
                ? jumpDestinations[random.Next(jumpDestinations.Count)]
                : random.Next(0, code.Count + 3);
            code[immediate] = (byte)(destination >> 8);
            code[immediate + 1] = (byte)destination;
        }

        // Sometimes cut the last opcode's immediates short.
        if (code.Count > 1 && random.Next(3) == 0) code.RemoveAt(code.Count - 1);
        return code.Count == 0 ? [(byte)Instruction.STOP] : code.ToArray();
    }

    private static byte SmallImmediate(Random random) => random.Next(5) switch
    {
        0 => (byte)random.Next(0, 4),
        1 => (byte)(random.Next(0, 9) * 32),
        2 => (byte)random.Next(250, 256),
        _ => (byte)random.Next(256),
    };

    /// <param name="initial">The frame's full policy, whose execution gas must be <paramref name="gas"/>; by default one holding only it.</param>
    private static unsafe Outcome Run(ulong gas, byte[] inputData, int head, CodeInfo codeInfo, Table table, IReleaseSpec? spec = null,
        bool loadsStorage = false, byte[]? returnData = null, bool isStatic = false, EthereumGasPolicy? initial = null)
    {
        DispatchingVirtualMachine vm = new(spec ?? ReleaseSpec);
        if (returnData is not null) vm.ReturnDataBuffer = returnData;
        using ExecutionEnvironment env = ExecutionEnvironment.Rent(codeInfo, Address.Zero, Address.Zero, null, 0, UInt256.Zero, inputData);
        using VmState<EthereumGasPolicy> frame = VmState<EthereumGasPolicy>.RentTopLevel(
            EthereumGasPolicy.FromULong(gas), ExecutionType.TRANSACTION, env, new StackAccessTracker(), default, isStatic);
        vm.Enter(frame);

        // A 32-byte aligned stack, as the dispatch loop hands the chain, filled with words small and large.
        byte[] stackBytes = GC.AllocateArray<byte>((EvmStack.MaxStackSize + 1) * EvmStack.WordSize, pinned: true);
        int alignment = (int)((nuint)Unsafe.AsPointer(ref stackBytes[0]) & (EvmStack.WordSize - 1));
        int start = alignment == 0 ? 0 : EvmStack.WordSize - alignment;
        Random values = new(head * 31 + codeInfo.CodeSpan.Length);
        for (int word = 0; word < head; word++)
        {
            int at = start + word * EvmStack.WordSize;
            switch (values.Next(8))
            {
                case 0: stackBytes[at + 31] = 1; break;
                case 1: stackBytes[at + 8] = 1; break;
                case 2: break;
                default:
                    stackBytes[at] = (byte)values.Next(256);
                    stackBytes[at + 1] = (byte)(values.Next(3) == 0 ? values.Next(0, 5) : 0);
                    break;
            }
        }

        // The table's declared entry type is the host's signature; its entries take the guest's.
        nint[] handlers = new nint[256];
        fixed (void* source = table switch
        {
            Table.Untraced => vm.GetOpcodeHandlers<OffFlag, OffFlag>(),
            Table.Cancelable => vm.GetOpcodeHandlers<OffFlag, OnFlag>(),
            _ => vm.GetOpcodeHandlers<OnFlag, OffFlag>(),
        })
            new ReadOnlySpan<nint>(source, handlers.Length).CopyTo(handlers);
        for (int opcode = 0; opcode < handlers.Length; opcode++)
        {
            if (NeedsWorldStateOrHash((Instruction)opcode) &&
                !(loadsStorage && (Instruction)opcode is Instruction.SLOAD or Instruction.TLOAD or Instruction.TSTORE) &&
                !(returnData is not null && (Instruction)opcode is Instruction.RETURNDATASIZE or Instruction.RETURNDATACOPY))
                handlers[opcode] = handlers[(int)Instruction.INVALID];
        }

        // On the heap, so the state that refers to it can be handed to a function pointer, whose parameters cannot be scoped.
        EthereumGasPolicy[] gasPolicy = [initial ?? EthereumGasPolicy.FromULong(gas)];
        EvmExceptionType exception;
        nint pc;
        nint finalHead;
        fixed (nint* entries = handlers)
        {
            EvmStack stack = new(head, vm.Tracer, ref stackBytes[start], codeInfo.ExecutionCodeSpan, codeInfo);
            stack.HoistInputData(inputData);
            VirtualMachine<EthereumGasPolicy>.DispatchState state = new() { Gas = ref gasPolicy[0], OpcodeHandlers = entries, Vm = vm, Memory = ref frame.Memory };
            exception = ((delegate*<ref EvmStack, ulong, ref VirtualMachine<EthereumGasPolicy>.DispatchState, ref byte, nint, nint*, ref byte, ref byte, EvmExceptionType>)
                entries[codeInfo.CodeSpan[0]])(ref stack, gas, ref state, ref stack.Code, stack.Head, entries, ref stack.Code, ref stack.Bottom);
            pc = state.FinalProgramCounter;
            finalHead = state.Head;
        }

        string stackHex = Convert.ToHexString(stackBytes, start, (int)Math.Clamp(finalHead, 0, EvmStack.MaxStackSize) * EvmStack.WordSize);
        ulong size = frame.Memory.Size;
        string memoryHex = "";
        if (!IsFault(exception))
        {
            Assert.That(frame.Memory.TryLoadSpan(UInt256.Zero, size, out Span<byte> memory), Is.True);
            memoryHex = Convert.ToHexString(memory);
        }

        return new Outcome(exception, EthereumGasPolicy.GetRemainingGas(in gasPolicy[0]), pc, finalHead, stackHex, memoryHex, size, gasPolicy[0]);
    }

    /// <summary>The value every test world state holds at <paramref name="key"/>.</summary>
    private static UInt256 StorageValueAt(UInt256 key) => key * 7 + 3;

    private static bool NeedsWorldStateOrHash(Instruction opcode) =>
        opcode is Instruction.KECCAK256 or Instruction.SLOAD or Instruction.SSTORE or Instruction.TLOAD or Instruction.TSTORE ||
        (opcode is >= Instruction.ADDRESS and <= (Instruction)0x4f &&
            opcode is not (Instruction.CALLDATALOAD or Instruction.CALLDATASIZE or Instruction.CALLDATACOPY or Instruction.CODESIZE or Instruction.CODECOPY)) ||
        opcode is >= Instruction.LOG0 and <= Instruction.LOG4 ||
        (opcode >= Instruction.CREATE && opcode != Instruction.INVALID);

    /// <summary>A virtual machine over a block of <paramref name="spec"/>, whose current frame a test can set.</summary>
    private sealed class DispatchingVirtualMachine : VirtualMachine<EthereumGasPolicy>
    {
        public DispatchingVirtualMachine(IReleaseSpec spec)
            : base(new NoBlockhashProvider(), new SingleReleaseSpecProvider(spec, BlockchainIds.Mainnet, BlockchainIds.Mainnet), LimboLogs.Instance)
        {
            SetBlockExecutionContext(new BlockExecutionContext(Header, spec));
            _txTracer = new SilentTracer();
            _worldState = Substitute.For<IWorldState>();
            _worldState.When(static state => state.Get(Arg.Any<StorageCell>(), out Arg.Any<UInt256>()))
                .Do(static call => call[1] = StorageValueAt(((StorageCell)call[0]).Index));
            Dictionary<StorageCell, UInt256> transient = [];
            _worldState.When(static state => state.SetTransientState(Arg.Any<StorageCell>(), Arg.Any<UInt256>()))
                .Do(call => transient[(StorageCell)call[0]] = (UInt256)call[1]);
            _worldState.When(static state => state.GetTransientState(Arg.Any<StorageCell>(), out Arg.Any<UInt256>()))
                .Do(call => call[1] = transient.GetValueOrDefault((StorageCell)call[0]));
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
