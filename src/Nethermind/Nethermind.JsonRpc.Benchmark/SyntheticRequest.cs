// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm;

namespace Nethermind.JsonRpc.Benchmark;

/// <summary>The synthetic <c>eth_call</c> of <see cref="EthCallStateOverrideBenchmarks"/>.</summary>
internal static class SyntheticRequest
{
    // The sizes of the 12 override codes, 80.8 KB in all; the called contract is the sixth.
    private static readonly int[] CodeSizes = [5_500, 15_100, 3_600, 2_800, 8_300, 6_100, 2_200, 13_200, 1_400, 5_500, 1_200, 15_900];
    private const int EntryIndex = 5;

    private const int CalldataSize = 11_364;
    private const int CalleeOutputSize = 9 * 32;
    private const int OutputSize = 3_424;

    private const int CalleeOutputArea = 0x1000;
    private const int EntryInputArea = 0x4000;

    /// <summary>The <c>params</c> array as UTF-8 JSON: the call, <c>latest</c>, and the state overrides.</summary>
    public static byte[] Parameters()
    {
        Random random = new(1);
        byte[][] addresses = new byte[CodeSizes.Length][];
        for (int i = 0; i < addresses.Length; i++) addresses[i] = Bytes(random, 20);

        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartArray();

            writer.WriteStartObject();
            writer.WriteString("from", "0x0000000000000000000000000000000000000000");
            writer.WriteString("to", addresses[EntryIndex].ToHexString(true));
            writer.WriteString("gas", "0x77359400");
            writer.WriteString("data", Calldata(random, addresses).ToHexString(true));
            writer.WriteEndObject();

            writer.WriteStringValue("latest");

            writer.WriteStartObject();
            for (int i = 0; i < addresses.Length; i++)
            {
                writer.WriteStartObject(addresses[i].ToHexString(true));
                byte[] code = i == EntryIndex ? EntryCode() : CalleeCode();
                writer.WriteString("code", Pad(code, CodeSizes[i], random).ToHexString(true));
                if (i == 0)
                {
                    writer.WriteStartObject("stateDiff");
                    writer.WriteString(new byte[32].ToHexString(true), Bytes(random, 32).ToHexString(true));
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();

            writer.WriteEndArray();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Throws unless the call returned what every contract it calls contributes.</summary>
    public static void Check(ResultWrapper<HexBytes> result)
    {
        if (result.Result.ResultType != ResultType.Success)
            throw new InvalidOperationException($"eth_call failed: {result.Result.Error}");

        ReadOnlySpan<byte> output = result.Data.Bytes.Span;
        if (output.Length != OutputSize)
            throw new InvalidOperationException($"eth_call returned {output.Length} B, not {OutputSize} B");

        for (int callee = 0; callee < CodeSizes.Length - 1; callee++)
        {
            if (!output.Slice(callee * CalleeOutputSize, CalleeOutputSize).ContainsAnyExcept((byte)0))
                throw new InvalidOperationException($"eth_call output has nothing from contract {callee}");
        }
    }

    // A selector, then for every contract but the called one: its address, the length of its input, and the input.
    private static byte[] Calldata(Random random, byte[][] addresses)
    {
        int callees = addresses.Length - 1;
        int inputWords = (CalldataSize - 4 - callees * 64) / 32;

        List<byte> calldata = [.. Bytes(random, 4)];
        for (int i = 0, callee = 0; i < addresses.Length; i++)
        {
            if (i == EntryIndex) continue;

            int length = 32 * (inputWords / callees + (callee++ < inputWords % callees ? 1 : 0));
            calldata.AddRange(Word(addresses[i]));
            calldata.AddRange(Word([(byte)(length >> 8), (byte)length]));
            calldata.AddRange(Bytes(random, length));
        }

        return calldata.Count == CalldataSize
            ? [.. calldata]
            : throw new InvalidOperationException($"calldata of {calldata.Count} B, not {CalldataSize} B");
    }

    // Calls each contract named in the calldata with its input, gathers their outputs from offset 0, returns 3,424 bytes.
    private static byte[] EntryCode()
    {
        Assembler a = new();
        a.Op(Instruction.PUSH0).Push(4);                                                   // ptr out
        a.Label("loop");
        a.Op(Instruction.CALLDATASIZE).Op(Instruction.DUP2).Op(Instruction.LT).Op(Instruction.ISZERO).PushLabel("done").Op(Instruction.JUMPI);
        a.Op(Instruction.DUP1).Push(32).Op(Instruction.ADD).Op(Instruction.CALLDATALOAD);   // len ptr out
        a.Op(Instruction.DUP1).Op(Instruction.DUP3).Push(64).Op(Instruction.ADD).Push(EntryInputArea).Op(Instruction.CALLDATACOPY);
        a.Push(CalleeOutputSize).Op(Instruction.DUP4).Op(Instruction.DUP3).Push(EntryInputArea).Op(Instruction.PUSH0);
        a.Op(Instruction.DUP7).Op(Instruction.CALLDATALOAD).Op(Instruction.GAS).Op(Instruction.CALL);
        a.Op(Instruction.ISZERO).PushLabel("fail").Op(Instruction.JUMPI);                  // len ptr out
        a.Push(64).Op(Instruction.ADD).Op(Instruction.ADD);                                // ptr' out
        a.Op(Instruction.SWAP1).Push(CalleeOutputSize).Op(Instruction.ADD).Op(Instruction.SWAP1);
        a.PushLabel("loop").Op(Instruction.JUMP);
        a.Label("done");
        a.Push(OutputSize).Op(Instruction.PUSH0).Op(Instruction.RETURN);
        a.Label("fail");
        a.Op(Instruction.PUSH0).Op(Instruction.PUSH0).Op(Instruction.REVERT);
        return a.Build();
    }

    // Hashes its input and storage slot 0, then returns nine words, each the hash of the one before and its index.
    private static byte[] CalleeCode()
    {
        Assembler a = new();
        a.Op(Instruction.CALLDATASIZE).Op(Instruction.PUSH0).Op(Instruction.PUSH0).Op(Instruction.CALLDATACOPY);
        a.Op(Instruction.CALLDATASIZE).Op(Instruction.PUSH0).Op(Instruction.KECCAK256);
        a.Op(Instruction.PUSH0).Op(Instruction.SLOAD).Op(Instruction.ADD).Op(Instruction.PUSH0); // i h
        a.Label("loop");
        a.Op(Instruction.DUP2).Op(Instruction.PUSH0).Op(Instruction.MSTORE);
        a.Op(Instruction.DUP1).Push(32).Op(Instruction.MSTORE);
        a.Push(64).Op(Instruction.PUSH0).Op(Instruction.KECCAK256).Op(Instruction.SWAP2).Op(Instruction.POP); // i h'
        a.Op(Instruction.DUP1).Push(5).Op(Instruction.SHL).Push(CalleeOutputArea).Op(Instruction.ADD);
        a.Op(Instruction.DUP3).Op(Instruction.SWAP1).Op(Instruction.MSTORE);
        a.Push(1).Op(Instruction.ADD);
        a.Op(Instruction.DUP1).Push(CalleeOutputSize / 32).Op(Instruction.GT).PushLabel("loop").Op(Instruction.JUMPI);
        a.Push(CalleeOutputSize).Push(CalleeOutputArea).Op(Instruction.RETURN);
        return a.Build();
    }

    // Instructions after the code, never reached, with immediates and jump destinations as compiled code has.
    private static byte[] Pad(byte[] code, int size, Random random)
    {
        ReadOnlySpan<Instruction> common =
        [
            Instruction.DUP1, Instruction.DUP2, Instruction.DUP3, Instruction.SWAP1, Instruction.SWAP2, Instruction.POP,
            Instruction.ADD, Instruction.SUB, Instruction.MUL, Instruction.AND, Instruction.EQ, Instruction.ISZERO,
            Instruction.MLOAD, Instruction.MSTORE, Instruction.SLOAD, Instruction.CALLDATALOAD, Instruction.JUMP,
            Instruction.JUMPI, Instruction.JUMPDEST, Instruction.SHL, Instruction.SHR, Instruction.KECCAK256,
        ];

        byte[] padded = new byte[size];
        code.CopyTo(padded, 0);
        int at = code.Length;
        while (at < size)
        {
            int pick = random.Next(10);
            if (pick < 4)
            {
                // PUSH1, PUSH2 and PUSH4 for offsets and selectors, now and then PUSH20 or PUSH32.
                int length = pick switch { 0 or 1 => 1, 2 => 2, _ => random.Next(4) switch { 0 => 20, 1 => 32, _ => 4 } };
                padded[at++] = (byte)(Instruction.PUSH1 + (byte)(length - 1));
                int immediate = Math.Min(length, size - at);
                random.NextBytes(padded.AsSpan(at, immediate));
                at += immediate;
            }
            else
            {
                padded[at++] = (byte)common[random.Next(common.Length)];
            }
        }

        return padded;
    }

    private static byte[] Word(ReadOnlySpan<byte> value)
    {
        byte[] word = new byte[32];
        value.CopyTo(word.AsSpan(32 - value.Length));
        return word;
    }

    private static byte[] Bytes(Random random, int length)
    {
        byte[] bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    private sealed class Assembler
    {
        private readonly List<byte> _code = [];
        private readonly Dictionary<string, int> _labels = [];
        private readonly List<(int At, string Label)> _jumps = [];

        public Assembler Op(Instruction instruction)
        {
            _code.Add((byte)instruction);
            return this;
        }

        public Assembler Push(int value)
        {
            if (value <= byte.MaxValue) return Op(Instruction.PUSH1).Byte(value);
            return Op(Instruction.PUSH2).Byte(value >> 8).Byte(value);
        }

        public Assembler PushLabel(string label)
        {
            Op(Instruction.PUSH2);
            _jumps.Add((_code.Count, label));
            return Byte(0).Byte(0);
        }

        public Assembler Label(string label)
        {
            _labels.Add(label, _code.Count);
            return Op(Instruction.JUMPDEST);
        }

        public byte[] Build()
        {
            foreach ((int at, string label) in _jumps)
            {
                int target = _labels[label];
                _code[at] = (byte)(target >> 8);
                _code[at + 1] = (byte)target;
            }

            return [.. _code];
        }

        private Assembler Byte(int value)
        {
            _code.Add((byte)value);
            return this;
        }
    }
}
