// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Evm;

namespace Nethermind.State.Flat.History.Test.Archive;

/// <summary>
/// The single contract for <see cref="ArchiveChainFixture"/>.
/// Sweeps a window of storage slots, writing the current block number into each, and reads a window back summing the values.
/// </summary>
/// <remarks>
/// Both halves live in one contract because the read must reach the storage the write produced, and
/// <c>SLOAD</c> only sees the executing contract's own slots. The written value is <c>NUMBER</c> so every block
/// changes every slot it touches: an unchanged slot produces no changeset entry and therefore no history row,
/// which would leave the generated index silently empty.
/// </remarks>
public static class StorageSweepContract
{
    /// <summary>Fixed address: the code is placed at genesis, so no deploy transaction and no nonce arithmetic.</summary>
    public static readonly Address Address = new("0x00000000000000000000000000000000000a5501");

    /// <summary>Gas an <c>SSTORE</c> of a fresh (zero-valued) slot costs, plus the surrounding loop.</summary>
    private const long WriteGasPerSlot = 25_000;

    /// <summary>Gas a cold <c>SLOAD</c> costs, plus the surrounding loop.</summary>
    private const long ReadGasPerSlot = 2_400;

    private const long IntrinsicGas = 30_000;

    public static byte[] RuntimeCode { get; } = BuildRuntimeCode();

    public static long WriteGas(int slotCount) => IntrinsicGas + slotCount * WriteGasPerSlot;

    public static long ReadGas(int slotCount) => IntrinsicGas + slotCount * ReadGasPerSlot;

    /// <summary>Writes <paramref name="slotCount"/> slots from <paramref name="firstSlot"/>, each set to the block number.</summary>
    public static byte[] WriteCallData(ulong firstSlot, int slotCount) => CallData(op: 0, firstSlot, slotCount);

    /// <summary>Reads <paramref name="slotCount"/> slots from <paramref name="firstSlot"/> and returns their sum.</summary>
    public static byte[] ReadCallData(ulong firstSlot, int slotCount) => CallData(op: 1, firstSlot, slotCount);

    private static byte[] CallData(ulong op, ulong firstSlot, int slotCount)
    {
        byte[] data = new byte[96];
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(24, 8), op);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(56, 8), firstSlot);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(88, 8), (ulong)slotCount);
        return data;
    }

    /// <summary>
    /// Calldata layout: word 0 selects write (zero) or read (non-zero), word 1 is the first slot, word 2 the count.
    /// </summary>
    private static byte[] BuildRuntimeCode()
    {
        const string read = "read";
        const string writeLoop = "writeLoop";
        const string writeEnd = "writeEnd";
        const string readLoop = "readLoop";
        const string readEnd = "readEnd";

        Assembler asm = new();

        // [] -> op, branch on it
        asm.Push1(0).Op(Instruction.CALLDATALOAD).PushLabel(read).Op(Instruction.JUMPI);

        // Write: stack is [slot, remaining] throughout the loop.
        LoadWindowArguments(asm);
        asm.Mark(writeLoop)
            .Op(Instruction.DUP1).Op(Instruction.ISZERO).PushLabel(writeEnd).Op(Instruction.JUMPI)
            // SSTORE pops the key first, so the key has to end up above the value.
            .Op(Instruction.DUP2).Op(Instruction.NUMBER).Op(Instruction.SWAP1).Op(Instruction.SSTORE);
        AdvanceWindow(asm);
        asm.PushLabel(writeLoop).Op(Instruction.JUMP)
            .Mark(writeEnd).Op(Instruction.STOP);

        // Read: stack is [slot, remaining, accumulator].
        asm.Mark(read);
        LoadWindowArguments(asm);
        asm.Push1(0)
            .Mark(readLoop)
            .Op(Instruction.DUP2).Op(Instruction.ISZERO).PushLabel(readEnd).Op(Instruction.JUMPI)
            .Op(Instruction.DUP3).Op(Instruction.SLOAD).Op(Instruction.ADD)
            // Bring the slot to the top, bump it, put it back under the accumulator.
            .Op(Instruction.SWAP2).Push1(1).Op(Instruction.ADD).Op(Instruction.SWAP2)
            // Same for the counter.
            .Op(Instruction.SWAP1).Push1(1).Op(Instruction.SWAP1).Op(Instruction.SUB).Op(Instruction.SWAP1)
            .PushLabel(readLoop).Op(Instruction.JUMP)
            .Mark(readEnd)
            .Push1(0).Op(Instruction.MSTORE)
            .Push1(32).Push1(0).Op(Instruction.RETURN);

        return asm.Done();
    }

    /// <summary>Pushes calldata words 1 and 2, leaving [slot, remaining].</summary>
    private static void LoadWindowArguments(Assembler asm) =>
        asm.Push1(32).Op(Instruction.CALLDATALOAD).Push1(64).Op(Instruction.CALLDATALOAD);

    /// <summary>Turns [slot, remaining] into [slot + 1, remaining - 1].</summary>
    private static void AdvanceWindow(Assembler asm) =>
        asm.Push1(1).Op(Instruction.SWAP1).Op(Instruction.SUB)
            .Op(Instruction.SWAP1).Push1(1).Op(Instruction.ADD).Op(Instruction.SWAP1);

    /// <summary>Emits bytecode with forward jump targets resolved on <see cref="Done"/>.</summary>
    private sealed class Assembler
    {
        private readonly List<byte> _code = [];
        private readonly Dictionary<string, int> _labels = [];
        private readonly List<(int Position, string Label)> _patches = [];

        public Assembler Op(Instruction instruction)
        {
            _code.Add((byte)instruction);
            return this;
        }

        public Assembler Push1(byte value)
        {
            _code.Add((byte)Instruction.PUSH1);
            _code.Add(value);
            return this;
        }

        public Assembler PushLabel(string label)
        {
            _code.Add((byte)Instruction.PUSH2);
            _patches.Add((_code.Count, label));
            _code.Add(0);
            _code.Add(0);
            return this;
        }

        public Assembler Mark(string label)
        {
            _labels[label] = _code.Count;
            return Op(Instruction.JUMPDEST);
        }

        public byte[] Done()
        {
            foreach ((int position, string label) in _patches)
            {
                int target = _labels[label];
                _code[position] = (byte)(target >> 8);
                _code[position + 1] = (byte)target;
            }

            return [.. _code];
        }
    }
}
