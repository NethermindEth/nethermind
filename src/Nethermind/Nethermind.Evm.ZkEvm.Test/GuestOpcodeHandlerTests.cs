// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.ZkEvm.Test;

/// <summary>Runs bytecode through the guest's untraced dispatch table, whose hottest entries are guest-only handlers.</summary>
/// <remarks>
/// Those handlers finish their common case inline and hand every other case to the shared handler, so each case
/// sits on a boundary where they choose: an analyzed or unanalyzed jump destination, a fused or unfused PUSH2,
/// memory that does or does not grow. A succeeding case also runs with every smaller amount of gas, each of which
/// has to run out, which puts a gas boundary inside every fused step. The chain is entered the way the dispatch
/// loop enters it, since a whole transaction cannot run here: a ZK_EVM build hashes through a zkVM precompile.
/// </remarks>
public class GuestOpcodeHandlerTests
{
    private const byte STOP = (byte)Instruction.STOP;
    private const byte SUB = (byte)Instruction.SUB;
    private const byte LT = (byte)Instruction.LT;
    private const byte GT = (byte)Instruction.GT;
    private const byte EQ = (byte)Instruction.EQ;
    private const byte SHL = (byte)Instruction.SHL;
    private const byte SHR = (byte)Instruction.SHR;
    private const byte ISZERO = (byte)Instruction.ISZERO;
    private const byte MLOAD = (byte)Instruction.MLOAD;
    private const byte MSTORE = (byte)Instruction.MSTORE;
    private const byte JUMP = (byte)Instruction.JUMP;
    private const byte JUMPI = (byte)Instruction.JUMPI;
    private const byte MSIZE = (byte)Instruction.MSIZE;
    private const byte JUMPDEST = (byte)Instruction.JUMPDEST;
    private const byte PUSH0 = (byte)Instruction.PUSH0;
    private const byte PUSH1 = (byte)Instruction.PUSH1;
    private const byte PUSH2 = (byte)Instruction.PUSH2;
    private const byte PUSH4 = (byte)Instruction.PUSH4;
    private const byte PUSH5 = (byte)Instruction.PUSH5;
    private const byte PUSH8 = (byte)Instruction.PUSH8;
    private const byte PUSH9 = (byte)Instruction.PUSH9;
    private const byte PUSH32 = (byte)Instruction.PUSH32;
    private const byte DUP1 = (byte)Instruction.DUP1;
    private const byte DUP2 = (byte)Instruction.DUP2;
    private const byte SWAP1 = (byte)Instruction.SWAP1;
    private const byte CALLDATALOAD = (byte)Instruction.CALLDATALOAD;
    private const byte KECCAK256 = (byte)Instruction.KECCAK256;
    private const byte INVALID = (byte)Instruction.INVALID;

    /// <summary>More gas than any failing case could spend before it faults.</summary>
    private const ulong AmpleGas = 100_000;

    private static readonly IReleaseSpec ReleaseSpec = Osaka.Instance;
    private static readonly ISpecProvider SpecProvider = new SingleReleaseSpecProvider(ReleaseSpec, BlockchainIds.Mainnet, BlockchainIds.Mainnet);
    private static readonly BlockHeader Header = new(Hash256.Zero, Hash256.Zero, Address.Zero, UInt256.Zero, 1, 30_000_000, 1, []);

    private static readonly byte[] WordA = Enumerable.Range(1, 32).Select(static b => (byte)b).ToArray();
    private static readonly byte[] WordB = Enumerable.Range(0xe0, 32).Select(static b => (byte)b).ToArray();

    private static IEnumerable<TestCaseData> Successes()
    {
        yield return Succeeds("JUMP onto a JUMPDEST", Code(PUSH1, 4, JUMP, INVALID, JUMPDEST, STOP), 3 + 8 + 1);

        // The loop jumps to one destination twice, so the second jump finds it analyzed.
        byte[] countdown = Code(
            PUSH1, 3,
            JUMPDEST,
            PUSH1, 1, SWAP1, SUB, DUP1, ISZERO,
            PUSH1, 15, JUMPI,
            PUSH1, 2, JUMP,
            JUMPDEST, STOP);
        const ulong countdownIteration = 1 + 3 + 3 + 3 + 3 + 3 + 3 + 10;
        const ulong countdownGas = 3 + 2 * (countdownIteration + 3 + 8) + countdownIteration + 1;
        yield return Succeeds("JUMP back to an analyzed destination", countdown, countdownGas);

        // The same loop with PUSH2 destinations: the first jump to each destination runs unfused, the later ones fuse.
        byte[] fusedCountdown = Code(
            PUSH1, 3,
            JUMPDEST,
            PUSH1, 1, SWAP1, SUB, DUP1, ISZERO,
            PUSH2, 0, 17, JUMPI,
            PUSH2, 0, 2, JUMP,
            JUMPDEST, STOP);
        yield return Succeeds("PUSH2 JUMP back to an analyzed destination", fusedCountdown, countdownGas);

        const ulong bottomTestedIteration = 1 + 3 + 3 + 3 + 3 + 3 + 10;
        yield return Succeeds("JUMPI back to an analyzed destination",
            Code(PUSH1, 3, JUMPDEST, PUSH1, 1, SWAP1, SUB, DUP1, PUSH1, 2, JUMPI, STOP), 3 + 3 * bottomTestedIteration);
        yield return Succeeds("PUSH2 JUMPI back to an analyzed destination",
            Code(PUSH1, 3, JUMPDEST, PUSH1, 1, SWAP1, SUB, DUP1, PUSH2, 0, 2, JUMPI, STOP), 3 + 3 * bottomTestedIteration);

        // Past the first 32 bytes the 32 bytes before a destination can prove it on their own, unless a byte among them may
        // be a PUSH that reaches it, as the 0x7f data byte of the PUSH1 before the last destination here may.
        yield return Succeeds("JUMP onto a JUMPDEST its look-back proves", Code([PUSH1, 48, JUMP, .. Filled(45, STOP), JUMPDEST, STOP]), 3 + 8 + 1);
        yield return Succeeds("JUMPI onto a JUMPDEST its look-back proves", Code([PUSH1, 1, PUSH1, 50, JUMPI, .. Filled(45, STOP), JUMPDEST, STOP]), 3 + 3 + 10 + 1);
        yield return Succeeds("JUMP onto a JUMPDEST just past PUSH32 data",
            Code([PUSH1, 48, JUMP, .. Filled(12, STOP), PUSH32, .. Filled(32, JUMPDEST), JUMPDEST, STOP]), 3 + 8 + 1);
        yield return Succeeds("JUMP onto a JUMPDEST its look-back cannot prove",
            Code([PUSH1, 48, JUMP, .. Filled(43, STOP), PUSH1, PUSH32, JUMPDEST, STOP]), 3 + 8 + 1);

        // Each loop branches back while its counter is non-zero: the first branch finds the destination unanalyzed and runs
        // unfused, the second fuses with it taken, the last fuses with it not taken.
        const ulong conditionIteration = 1 + 3 + 3 + 3 + 3 + 3 + 3 + 3 + 10;
        yield return Succeeds("LT PUSH2 JUMPI loop",
            Code(PUSH1, 3, JUMPDEST, PUSH1, 1, SWAP1, SUB, DUP1, PUSH1, 0, LT, PUSH2, 0, 2, JUMPI, STOP), 3 + 3 * conditionIteration);
        yield return Succeeds("GT PUSH2 JUMPI loop",
            Code(PUSH1, 3, JUMPDEST, PUSH1, 1, SWAP1, SUB, PUSH1, 0, DUP2, GT, PUSH2, 0, 2, JUMPI, STOP), 3 + 3 * conditionIteration);
        yield return Succeeds("ISZERO ISZERO PUSH2 JUMPI loop",
            Code(PUSH1, 3, JUMPDEST, PUSH1, 1, SWAP1, SUB, DUP1, ISZERO, ISZERO, PUSH2, 0, 2, JUMPI, STOP), 3 + 3 * conditionIteration);
        yield return Succeeds("EQ ISZERO PUSH2 JUMPI loop",
            Code(PUSH1, 3, JUMPDEST, PUSH1, 1, SWAP1, SUB, DUP1, PUSH1, 0, EQ, ISZERO, PUSH2, 0, 2, JUMPI, STOP), 3 + 3 * (conditionIteration + 3));
        yield return Succeeds("EQ PUSH2 JUMPI not taken onto an invalid destination",
            Code(PUSH1, 1, PUSH1, 2, EQ, PUSH2, 0, 0x3f, JUMPI, STOP), 3 + 3 + 3 + 3 + 10);
        yield return Succeeds("EQ PUSH2 JUMPI not taken on a stack one short of full",
            Code([.. Filled(1023, PUSH0), PUSH1, 1, EQ, PUSH2, 0, 0x3f, JUMPI, STOP]), 1023 * 2 + 3 + 3 + 3 + 10);

        // A dispatcher run twice: the first entry passes over a selector and finds the second's destination unanalyzed,
        // so its match runs unfused; the second entry passes over and matches fused.
        byte[] dispatcher = Code(
            PUSH1, 2, PUSH4, 0xaa, 0xbb, 0xcc, 0xdd,
            JUMPDEST,
            DUP1, PUSH4, 0x11, 0x22, 0x33, 0x44, EQ, PUSH2, 0, 0x30, JUMPI,
            DUP1, PUSH4, 0xaa, 0xbb, 0xcc, 0xdd, EQ, PUSH2, 0, 31, JUMPI,
            STOP,
            JUMPDEST, SWAP1, PUSH1, 1, SWAP1, SUB, DUP1, PUSH2, 0, 43, JUMPI,
            STOP,
            JUMPDEST, SWAP1, PUSH2, 0, 7, JUMP);
        const ulong dispatch = 3 + 3 + 3 + 3 + 10;
        const ulong function = 1 + 3 + 3 + 3 + 3 + 3 + 3 + 10;
        yield return Succeeds("DUP1 PUSH4 EQ PUSH2 JUMPI dispatcher",
            dispatcher, 3 + 3 + (1 + dispatch + dispatch + function + 1 + 3 + 3 + 8 + 1) + (dispatch + dispatch + function));
        byte[] highSelector = new byte[32];
        highSelector[0] = 1;
        highSelector[31] = 0xdd;
        yield return Succeeds("DUP1 PUSH4 EQ PUSH2 JUMPI passing a word with a high limb",
            Code([PUSH32, .. highSelector, DUP1, PUSH4, 0, 0, 0, 0xdd, EQ, PUSH2, 0, 0x3f, JUMPI, STOP]), 3 + dispatch);

        // Unfused, each comparison leaves its result; operands that differ only in a high limb exercise the whole compare.
        byte[] high = new byte[32];
        high[0] = 1;
        yield return Succeeds("EQ and ISZERO results",
            Code([PUSH32, .. high, PUSH1, 0, EQ, ISZERO, PUSH1, 0, MSTORE, STOP]), 3 + 3 + 3 + 3 + 3 + (3 + 3),
            [.. new byte[31], 1]);
        yield return Succeeds("LT result on a high limb",
            Code([PUSH32, .. high, PUSH1, 1, LT, PUSH1, 0, MSTORE, STOP]), 3 + 3 + 3 + 3 + (3 + 3),
            [.. new byte[31], 1]);
        yield return Succeeds("GT result on a high limb",
            Code([PUSH32, .. high, PUSH1, 1, GT, PUSH1, 0, MSTORE, STOP]), 3 + 3 + 3 + 3 + (3 + 3),
            new byte[32]);

        // A JUMPI that is not taken never validates its destination.
        yield return Succeeds("JUMPI not taken onto an invalid destination", Code(PUSH1, 0, PUSH1, 0x3f, JUMPI, STOP), 3 + 3 + 10);
        yield return Succeeds("PUSH2 JUMPI not taken onto an invalid destination", Code(PUSH1, 0, PUSH2, 0, 0x3f, JUMPI, STOP), 3 + 3 + 10);

        byte[] topBitOnly = new byte[32];
        topBitOnly[0] = 0x80;
        yield return Succeeds("JUMPI taken on a condition in its top limb",
            Code([PUSH32, .. topBitOnly, PUSH1, 37, JUMPI, INVALID, JUMPDEST, STOP]), 3 + 3 + 10 + 1);

        // The shared PUSH handlers zero-extend missing immediates, and the code ends without another opcode.
        yield return Succeeds("PUSH1 without an immediate", Code(PUSH1), 3);
        yield return Succeeds("PUSH1 ending the code", Code(PUSH1, 1), 3);
        yield return Succeeds("PUSH1 before an opcode", Code(PUSH1, 0xa5, PUSH1, 0, MSTORE, STOP), 3 + 3 + (3 + 3),
            [.. new byte[31], 0xa5]);
        yield return Succeeds("PUSH1 onto a stack one short of full", Code([.. Filled(1023, PUSH0), PUSH1, 0, STOP]), 1023 * 2 + 3);
        yield return Succeeds("PUSH2 without immediates", Code(PUSH2), 3);
        yield return Succeeds("PUSH2 with one immediate", Code(PUSH2, 1), 3);
        yield return Succeeds("PUSH2 ending the code", Code(PUSH2, 1, 2), 3);
        yield return Succeeds("PUSH2 onto a stack one short of full", Code([.. Filled(1023, PUSH0), PUSH2, 0, 0, STOP]), 1023 * 2 + 3);

        // Memory starts empty, so the first store grows it; the next two reuse it and the last grows it again.
        yield return Succeeds("MSTORE and MLOAD round trip",
            Code([
                PUSH32, .. WordA, PUSH1, 0, MSTORE,
                PUSH32, .. WordB, PUSH1, 0, MSTORE,
                PUSH1, 0, MLOAD,
                PUSH1, 0x20, MSTORE,
                STOP]),
            (3 + 3 + 3 + 3) + (3 + 3 + 3) + (3 + 3) + (3 + 3 + 3),
            [.. WordB, .. WordB]);

        yield return Succeeds("MSTORE and MLOAD at unaligned offsets",
            Code([
                PUSH32, .. WordA, PUSH1, 1, MSTORE,
                PUSH1, 0, MLOAD,
                PUSH1, 0x41, MSTORE,
                STOP]),
            (3 + 3 + 3 + 6) + (3 + 3) + (3 + 3 + 6),
            [0, .. WordA, .. new byte[32], 0, .. WordA[..31], .. new byte[31]]);

        yield return Succeeds("MLOAD past the active memory",
            Code(PUSH1, 0x40, MLOAD, PUSH1, 0, MSTORE, STOP),
            (3 + 3 + 9) + (3 + 3),
            new byte[96]);

        // 992 + 32 is the end of the frame's inline memory, so the first word needs no new backing and the second does.
        yield return Succeeds("MSTORE of the last inline word",
            Code([PUSH32, .. WordA, PUSH2, 0x03, 0xe0, MSTORE, PUSH2, 0x03, 0xe0, MLOAD, PUSH1, 0, MSTORE, STOP]),
            (3 + 3 + 3 + 98) + (3 + 3) + (3 + 3),
            [.. WordA, .. new byte[992 - 32], .. WordA]);
        yield return Succeeds("MSTORE past the inline memory",
            Code([PUSH32, .. WordA, PUSH2, 0x03, 0xe8, MSTORE, PUSH2, 0x03, 0xe8, MLOAD, PUSH1, 0, MSTORE, STOP]),
            (3 + 3 + 3 + 101) + (3 + 3) + (3 + 3),
            [.. WordA, .. new byte[1000 - 32], .. WordA, .. new byte[24]]);

        // Each store writes the memory size at the memory size, growing memory a word at a time through the inline memory and past it.
        const int growingStores = 33;
        byte[] growth = new byte[growingStores * 32];
        for (int word = 0; word < growingStores; word++)
        {
            int offset = word * 32;
            growth[offset + 30] = (byte)(offset >> 8);
            growth[offset + 31] = (byte)offset;
        }

        yield return Succeeds("MSTORE growing memory a word at a time",
            Code([.. Repeated(growingStores, MSIZE, MSIZE, MSTORE), STOP]),
            growingStores * (2 + 2 + 3) + MemoryCost(growingStores),
            growth);

        // A store that starts inside the initialized memory extends it, unless it would outgrow the frame's inline memory.
        yield return Succeeds("MSTORE overlapping the end of the initialized memory",
            Code([PUSH32, .. WordA, PUSH1, 0, MSTORE, PUSH32, .. WordB, PUSH1, 16, MSTORE, STOP]),
            (3 + 3 + 3 + 3) + (3 + 3 + 3 + 3),
            [.. WordA[..16], .. WordB, .. new byte[16]]);
        byte[] overflow = [.. growth[..1000], .. WordA, .. new byte[24]];
        yield return Succeeds("MSTORE from the initialized memory past the inline memory",
            Code([.. Repeated(growingStores - 1, MSIZE, MSIZE, MSTORE), PUSH32, .. WordA, PUSH2, 0x03, 0xe8, MSTORE, STOP]),
            (growingStores - 1) * (2 + 2 + 3) + (3 + 3 + 3) + MemoryCost(growingStores),
            overflow);

        // Words wholly inside the input data, one ending at its last byte, and ones that run past it or start past it,
        // which read as zero-padded.
        byte[] input = Enumerable.Range(1, 40).Select(static b => (byte)b).ToArray();
        byte[] padded = [.. input, .. new byte[256]];
        foreach (int offset in (int[])[0, 8, 9, 39, 40, 200])
        {
            yield return Succeeds($"CALLDATALOAD at {offset}", Code(PUSH1, (byte)offset, CALLDATALOAD, PUSH1, 0, MSTORE, STOP),
                3 + 3 + (3 + 3 + 3), padded[offset..(offset + 32)], input);
        }

        yield return Succeeds("CALLDATALOAD at 2^32", Code(PUSH5, 1, 0, 0, 0, 0, CALLDATALOAD, PUSH1, 0, MSTORE, STOP), 3 + 3 + (3 + 3 + 3), new byte[32], input);
        yield return Succeeds("CALLDATALOAD of no input", Code(PUSH1, 0, CALLDATALOAD, PUSH1, 0, MSTORE, STOP), 3 + 3 + (3 + 3 + 3), new byte[32]);

        // Every limb offset with a zero, a partial and a whole-limb bit shift, and amounts past the word, including
        // ones only a high limb makes large. The word's top bit is set, which tells a logical SHR from an arithmetic one.
        BigInteger shifted = new(WordB, isUnsigned: true, isBigEndian: true);
        BigInteger[] amounts = [0, 1, 63, 64, 65, 96, 127, 128, 160, 191, 192, 224, 255, 256, 257, BigInteger.One << 64, BigInteger.One << 255];
        foreach (BigInteger amount in amounts)
        {
            string name = amount > ushort.MaxValue ? $"2^{(int)BigInteger.Log(amount, 2)}" : amount.ToString();
            const ulong shiftGas = 3 + 3 + 3 + (3 + 3 + 3);
            yield return Succeeds($"SHL by {name}", Code([PUSH32, .. WordB, PUSH32, .. Word(amount), SHL, PUSH1, 0, MSTORE, STOP]),
                shiftGas, Word(amount < 256 ? shifted << (int)amount : BigInteger.Zero));
            yield return Succeeds($"SHR by {name}", Code([PUSH32, .. WordB, PUSH32, .. Word(amount), SHR, PUSH1, 0, MSTORE, STOP]),
                shiftGas, Word(amount < 256 ? shifted >> (int)amount : BigInteger.Zero));
        }
    }

    private static IEnumerable<TestCaseData> Failures()
    {
        yield return Fails("JUMP onto a non-JUMPDEST byte", Code(PUSH1, 3, JUMP, STOP), EvmExceptionType.InvalidJumpDestination);
        yield return Fails("JUMP into PUSH data", Code(PUSH1, 4, JUMP, PUSH2, JUMPDEST, JUMPDEST, STOP), EvmExceptionType.InvalidJumpDestination);
        yield return Fails("JUMP past the code", Code(PUSH1, 0x10, JUMP, JUMPDEST), EvmExceptionType.InvalidJumpDestination);
        // Both destinations name a JUMPDEST in their low bits: 8 and 12.
        yield return Fails("JUMP to a destination above 32 bits", Code(PUSH5, 1, 0, 0, 0, 8, JUMP, STOP, JUMPDEST, STOP), EvmExceptionType.InvalidJumpDestination);
        yield return Fails("JUMP to a destination above 64 bits", Code(PUSH9, 1, 0, 0, 0, 0, 0, 0, 0, 12, JUMP, STOP, JUMPDEST, STOP), EvmExceptionType.InvalidJumpDestination);
        yield return Fails("JUMP on an empty stack", Code(JUMP), EvmExceptionType.StackUnderflow);
        yield return Fails("JUMP onto the last byte of PUSH32 data",
            Code([PUSH1, 48, JUMP, .. Filled(13, STOP), PUSH32, .. Filled(31, STOP), JUMPDEST, STOP]), EvmExceptionType.InvalidJumpDestination);

        yield return Fails("JUMPI onto a non-JUMPDEST byte", Code(PUSH1, 1, PUSH1, 5, JUMPI, STOP), EvmExceptionType.InvalidJumpDestination);
        yield return Fails("JUMPI into PUSH data", Code(PUSH1, 1, PUSH1, 6, JUMPI, PUSH2, JUMPDEST, JUMPDEST, STOP), EvmExceptionType.InvalidJumpDestination);
        yield return Fails("JUMPI with only a destination", Code(PUSH1, 3, JUMPI, JUMPDEST), EvmExceptionType.StackUnderflow);
        yield return Fails("JUMPI onto the last byte of PUSH32 data",
            Code([PUSH1, 1, PUSH1, 50, JUMPI, .. Filled(13, STOP), PUSH32, .. Filled(31, STOP), JUMPDEST, STOP]), EvmExceptionType.InvalidJumpDestination);

        yield return Fails("PUSH2 JUMP onto a non-JUMPDEST byte", Code(PUSH2, 0, 4, JUMP, STOP), EvmExceptionType.InvalidJumpDestination);
        yield return Fails("PUSH2 JUMPI onto a non-JUMPDEST byte", Code(PUSH1, 1, PUSH2, 0, 6, JUMPI, STOP), EvmExceptionType.InvalidJumpDestination);
        yield return Fails("PUSH2 JUMPI on an empty stack", Code(PUSH2, 0, 4, JUMPI, JUMPDEST), EvmExceptionType.StackUnderflow);
        yield return Fails("PUSH1 onto a full stack", Code([.. Filled(1024, PUSH0), PUSH1, 0, STOP]), EvmExceptionType.StackOverflow);
        // The branch's PUSH2 overflows the stack an ISZERO leaves full, so the comparison runs unfused before it faults.
        yield return Fails("ISZERO PUSH2 JUMPI on a full stack", Code([.. Filled(1024, PUSH0), ISZERO, PUSH2, 0, 0x3f, JUMPI, STOP]), EvmExceptionType.StackOverflow);
        yield return Fails("EQ PUSH2 JUMPI taken onto a non-JUMPDEST byte", Code(PUSH1, 2, PUSH1, 2, EQ, PUSH2, 0, 9, JUMPI, STOP), EvmExceptionType.InvalidJumpDestination);
        yield return Fails("ISZERO on an empty stack", Code(ISZERO), EvmExceptionType.StackUnderflow);
        yield return Fails("DUP1 on an empty stack", Code(DUP1), EvmExceptionType.StackUnderflow);
        yield return Fails("DUP1 onto a full stack", Code([.. Filled(1024, PUSH0), DUP1]), EvmExceptionType.StackOverflow);
        // DUP1 fits but the selector's PUSH4 does not, so DUP1 runs unfused before the push faults.
        yield return Fails("DUP1 PUSH4 EQ PUSH2 JUMPI on a stack one short of full",
            Code([.. Filled(1023, PUSH0), DUP1, PUSH4, 0, 0, 0, 0, EQ, PUSH2, 0, 0, JUMPI]), EvmExceptionType.StackOverflow);
        yield return Fails("EQ with one operand", Code(PUSH1, 1, EQ, PUSH2, 0, 0, JUMPI), EvmExceptionType.StackUnderflow);
        yield return Fails("LT with one operand", Code(PUSH1, 1, LT), EvmExceptionType.StackUnderflow);
        yield return Fails("GT with one operand", Code(PUSH1, 1, GT), EvmExceptionType.StackUnderflow);
        yield return Fails("PUSH2 onto a full stack", Code([.. Filled(1024, PUSH0), PUSH2, 0, 0, STOP]), EvmExceptionType.StackOverflow);
        // A following jump does not exempt the push from the stack limit.
        yield return Fails("PUSH2 JUMP onto a full stack", Code([.. Filled(1024, PUSH0), PUSH2, 0x04, 0x04, JUMP, JUMPDEST]), EvmExceptionType.StackOverflow);

        yield return Fails("MSTORE with one operand", Code(PUSH1, 0, MSTORE), EvmExceptionType.StackUnderflow);
        yield return Fails("MLOAD on an empty stack", Code(MLOAD), EvmExceptionType.StackUnderflow);
        yield return Fails("MSTORE at 2^32", Code(PUSH1, 1, PUSH5, 1, 0, 0, 0, 0, MSTORE), EvmExceptionType.OutOfGas);
        yield return Fails("MSTORE at 2^64", Code(PUSH1, 1, PUSH9, 1, 0, 0, 0, 0, 0, 0, 0, 0, MSTORE), EvmExceptionType.OutOfGas);
        yield return Fails("MLOAD at 2^64 - 1", Code(PUSH8, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, MLOAD), EvmExceptionType.OutOfGas);

        yield return Fails("CALLDATALOAD on an empty stack", Code(CALLDATALOAD), EvmExceptionType.StackUnderflow);

        // Only the paths that never reach the hash run here: a ZK_EVM test process cannot hash.
        yield return Fails("KECCAK256 with one operand", Code(PUSH1, 0, KECCAK256), EvmExceptionType.StackUnderflow);
        yield return Fails("KECCAK256 at 2^32", Code(PUSH1, 1, PUSH5, 1, 0, 0, 0, 0, KECCAK256), EvmExceptionType.OutOfGas);
        yield return Fails("KECCAK256 of 2^32 bytes", Code(PUSH5, 1, 0, 0, 0, 0, PUSH1, 0, KECCAK256), EvmExceptionType.OutOfGas);
        yield return Fails("SHL with one operand", Code(PUSH1, 1, SHL), EvmExceptionType.StackUnderflow);
        yield return Fails("SHR with one operand", Code(PUSH1, 1, SHR), EvmExceptionType.StackUnderflow);
    }

    [TestCaseSource(nameof(Successes))]
    public void Succeeds_on_exactly_its_gas(byte[] code, ulong gasUsed, byte[] memory, byte[] inputData) =>
        AssertSucceedsOnExactlyItsGas(code, gasUsed, memory, inputData);

    /// <remarks>
    /// A ZK_EVM test process cannot run the zkVM's keccak, so the range's bytes are memoized with a stand-in digest
    /// first: hashing any other bytes misses the memo and reaches the precompile, which throws. That leaves out the
    /// empty range, whose digest the guest takes from a constant hashed on first use. Memory holds 64 initialized
    /// bytes, so a range may end on that boundary or run past it, which grows memory through the shared handler.
    /// </remarks>
    [Test]
    public void Keccak256_leaves_the_digest_of_its_range([Values(8, 32, 33, 64)] int length, [Values(0, 5, 32)] int offset)
    {
        byte[] memory = [.. WordA, .. WordB, .. new byte[32]];
        byte[] digest = Enumerable.Range(0, 32).Select(b => (byte)(0x80 + b + length + offset)).ToArray();
        KeccakCache.WriteMemo(memory.AsSpan(offset, length), new ValueHash256(digest));
        ulong hashGas = GasCostOf.Sha3 + GasCostOf.Sha3Word * (ulong)((length + 31) / 32);

        AssertSucceedsOnExactlyItsGas(
            Code([
                PUSH32, .. WordA, PUSH1, 0, MSTORE,
                PUSH32, .. WordB, PUSH1, 32, MSTORE,
                PUSH1, (byte)length, PUSH1, (byte)offset, KECCAK256,
                PUSH1, 64, MSTORE,
                STOP]),
            (3 + 3 + 3) + (3 + 3 + 3) + (3 + 3 + hashGas) + (3 + 3) + MemoryCost(3),
            [.. WordA, .. WordB, .. digest],
            []);
    }

    [TestCaseSource(nameof(Failures))]
    public void Fails_as_the_shared_handlers_do(byte[] code, EvmExceptionType exception) =>
        Assert.That(Run(code, AmpleGas).Exception, Is.EqualTo(exception));

    [Test]
    public void Keccak256_of_active_memory_short_of_gas_runs_out()
    {
        const ulong store = 3 + 3 + 3 + 3;
        const ulong operands = 3 + 3;
        const ulong hash = GasCostOf.Sha3 + GasCostOf.Sha3Word;
        byte[] code = Code(PUSH1, 1, PUSH1, 0, MSTORE, PUSH1, 32, PUSH1, 0, KECCAK256, STOP);

        Assert.That(Run(code, store + operands + hash - 1).Exception, Is.EqualTo(EvmExceptionType.OutOfGas));
    }

    /// <remarks>
    /// Once the first pass has analyzed the destination, every later iteration runs the fused step, and the range
    /// of gas lets it run out at each of the charges inside it.
    /// </remarks>
    [Test]
    public void Endless_loop_runs_out_of_gas_at_every_charge(
        [Values("PUSH2 JUMP", "PUSH2 JUMPI", "JUMP", "JUMPI")] string loop,
        [Range(200, 220)] int gas)
    {
        byte[] code = loop switch
        {
            "PUSH2 JUMP" => Code(PUSH1, 4, JUMP, INVALID, JUMPDEST, PUSH2, 0, 4, JUMP),
            "PUSH2 JUMPI" => Code(JUMPDEST, PUSH1, 1, PUSH2, 0, 0, JUMPI),
            "JUMP" => Code(JUMPDEST, PUSH1, 0, JUMP),
            _ => Code(JUMPDEST, PUSH1, 1, PUSH1, 0, JUMPI),
        };

        Assert.That(Run(code, (ulong)gas).Exception, Is.EqualTo(EvmExceptionType.OutOfGas));
    }

    private static void AssertSucceedsOnExactlyItsGas(byte[] code, ulong gasUsed, byte[] memory, byte[] inputData)
    {
        Outcome outcome = Run(code, gasUsed, inputData);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Exception, Is.AnyOf(EvmExceptionType.None, EvmExceptionType.Stop));
            Assert.That(outcome.GasLeft, Is.Zero);
            Assert.That(outcome.Memory, Is.EqualTo(memory));
        }

        for (ulong gas = 0; gas < gasUsed; gas++)
            Assert.That(Run(code, gas, inputData).Exception, Is.EqualTo(EvmExceptionType.OutOfGas), $"with {gas} gas");
    }

    private static unsafe Outcome Run(byte[] code, ulong gas, byte[]? inputData = null)
    {
        DispatchingVirtualMachine vm = new();
        CodeInfo codeInfo = new(code);
        using ExecutionEnvironment env = ExecutionEnvironment.Rent(codeInfo, Address.Zero, Address.Zero, null, 0, UInt256.Zero, inputData ?? []);
        // Rented as a transaction's frame is, so its memory starts with nothing initialized, as the guest's does.
        VmState<EthereumGasPolicy> frame = VmState<EthereumGasPolicy>.RentTopLevel(
            EthereumGasPolicy.FromULong(gas), ExecutionType.TRANSACTION, env, new StackAccessTracker(), default);
        vm.Enter(frame);

        // The dispatch loop hands the chain a 32-byte aligned stack.
        byte[] stackBytes = GC.AllocateArray<byte>((EvmStack.MaxStackSize + 1) * EvmStack.WordSize, pinned: true);
        int alignment = (int)((nuint)Unsafe.AsPointer(ref stackBytes[0]) & (EvmStack.WordSize - 1));
        ref byte stackStart = ref stackBytes[alignment == 0 ? 0 : EvmStack.WordSize - alignment];

        // On the heap, so the state that refers to it can be handed to a function pointer, whose parameters cannot be scoped.
        EthereumGasPolicy[] gasPolicy = [EthereumGasPolicy.FromULong(gas)];
        EvmExceptionType exception;
        // The table's declared entry type is the host's signature; its entries take the guest's.
        fixed (void* entries = vm.GetOpcodeHandlers<OffFlag, OffFlag>())
        {
            nint* table = (nint*)entries;
            // The code info's copy of the code is the one followed by the padding that dispatch may read.
            EvmStack stack = new(0, ref stackStart, codeInfo.CodeSpan, codeInfo);
            VirtualMachine<EthereumGasPolicy>.DispatchState state = new() { Gas = ref gasPolicy[0], OpcodeHandlers = table, Vm = vm };
            exception = ((delegate*<ref EvmStack, ulong, ref VirtualMachine<EthereumGasPolicy>.DispatchState, nint, nint, nint*, ref byte, nint, EvmExceptionType>)table[code[0]])(
                ref stack, gas, ref state, 0, stack.Head, table, ref stack.Code, stack.CodeLength);
        }

        ulong size = frame.Memory.Size;
        Assert.That(frame.Memory.TryLoadSpan(UInt256.Zero, size, out Span<byte> memory), Is.True);
        return new Outcome(exception, EthereumGasPolicy.GetRemainingGas(in gasPolicy[0]), memory.ToArray());
    }

    /// <summary>The Yellow Paper memory cost of <paramref name="words"/> active words.</summary>
    private static ulong MemoryCost(ulong words) => words * GasCostOf.Memory + words * words / 512;

    private static TestCaseData Succeeds(string name, byte[] code, ulong gasUsed, byte[]? memory = null, byte[]? inputData = null) =>
        new TestCaseData(code, gasUsed, memory ?? [], inputData ?? []).SetName($"{{m}}({name})");

    private static TestCaseData Fails(string name, byte[] code, EvmExceptionType exception) =>
        new TestCaseData(code, exception).SetName($"{{m}}({name})");

    private static byte[] Code(params byte[] code) => code;

    /// <summary>The low 256 bits of <paramref name="value"/> as a big-endian word.</summary>
    private static byte[] Word(BigInteger value)
    {
        byte[] bytes = (value & ((BigInteger.One << 256) - 1)).ToByteArray(isUnsigned: true, isBigEndian: true);
        return [.. new byte[32 - bytes.Length], .. bytes];
    }

    private static byte[] Filled(int length, byte op) => Enumerable.Repeat(op, length).ToArray();

    private static byte[] Repeated(int times, params byte[] ops) => Enumerable.Repeat(ops, times).SelectMany(static o => o).ToArray();

    private readonly record struct Outcome(EvmExceptionType Exception, ulong GasLeft, byte[] Memory);

    /// <summary>A virtual machine over a block of <see cref="ReleaseSpec"/>, whose current frame a test can set.</summary>
    private sealed class DispatchingVirtualMachine : VirtualMachine<EthereumGasPolicy>
    {
        public DispatchingVirtualMachine() : base(new NoBlockhashProvider(), SpecProvider, LimboLogs.Instance) =>
            SetBlockExecutionContext(new BlockExecutionContext(Header, ReleaseSpec));

        public void Enter(VmState<EthereumGasPolicy> frame) => VmState = frame;
    }

    private sealed class NoBlockhashProvider : IBlockhashProvider
    {
        public Hash256? GetBlockhash(BlockHeader currentBlock, ulong number, IReleaseSpec spec) => null;

        public System.Threading.Tasks.Task Prefetch(BlockHeader currentBlock, System.Threading.CancellationToken token) =>
            System.Threading.Tasks.Task.CompletedTask;
    }
}
