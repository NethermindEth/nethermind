// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Evm.InstructionsBenchmark.Helpers;
using Nethermind.Int256;

namespace Nethermind.Evm.InstructionsBenchmark.Bitwise;

/// <summary>
/// Benchmarks for BYTE opcode - extracts a single byte from a 256-bit word.
/// Run: dotnet run -c Release --filter "*ByteBenchmark*"
/// </summary>
[DisassemblyDiagnoser(maxDepth: 3, exportGithubMarkdown: true, exportHtml: true)]
public class ByteBenchmark
{
    private byte[] _stackBuffer = null!;
    private byte[] _codeBuffer = null!;

    // Test value with distinct bytes for easy verification
    private static readonly UInt256 TestValue = UInt256.Parse("0x0001020304050607_08090A0B0C0D0E0F_1011121314151617_18191A1B1C1D1E1F");

    [GlobalSetup]
    public void Setup()
    {
        _stackBuffer = BenchmarkHelpers.CreateStackBuffer();
        _codeBuffer = new byte[1];
    }

    private void SetupByteStack(uint position, in UInt256 value)
    {
        // Stack: [value, position] (position is popped first)
        BenchmarkHelpers.WriteStackSlot(_stackBuffer, 0, value);
        BenchmarkHelpers.ClearStackSlot(_stackBuffer, 1);
        BenchmarkHelpers.WriteStackSlotU64(_stackBuffer, 1, position);
    }

    [Benchmark(Baseline = true)]
    public byte Byte_Position0()
    {
        SetupByteStack(0, in TestValue);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        // Pop position and value
        stack.TryPopSmallIndex(out uint position);
        ref byte bytes = ref stack.PopBytesByRef();

        // Extract byte at position (big-endian)
        byte result = position < 32 ? Unsafe.Add(ref bytes, (nuint)position) : (byte)0;
        stack.PushByte<OffFlag>(result);

        return result;
    }

    [Benchmark]
    public byte Byte_Position15()
    {
        SetupByteStack(15, in TestValue);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.TryPopSmallIndex(out uint position);
        ref byte bytes = ref stack.PopBytesByRef();
        byte result = position < 32 ? Unsafe.Add(ref bytes, (nuint)position) : (byte)0;
        stack.PushByte<OffFlag>(result);

        return result;
    }

    [Benchmark]
    public byte Byte_Position31()
    {
        SetupByteStack(31, in TestValue);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.TryPopSmallIndex(out uint position);
        ref byte bytes = ref stack.PopBytesByRef();
        byte result = position < 32 ? Unsafe.Add(ref bytes, (nuint)position) : (byte)0;
        stack.PushByte<OffFlag>(result);

        return result;
    }

    [Benchmark]
    public byte Byte_Position32_OutOfRange()
    {
        SetupByteStack(32, in TestValue);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.TryPopSmallIndex(out uint position);
        ref byte bytes = ref stack.PopBytesByRef();
        byte result = position < 32 ? Unsafe.Add(ref bytes, (nuint)position) : (byte)0;
        stack.PushByte<OffFlag>(result);

        return result;
    }

    [Benchmark]
    public byte Byte_Position255_OutOfRange()
    {
        SetupByteStack(255, in TestValue);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.TryPopSmallIndex(out uint position);
        ref byte bytes = ref stack.PopBytesByRef();
        byte result = position < 32 ? Unsafe.Add(ref bytes, (nuint)position) : (byte)0;
        stack.PushByte<OffFlag>(result);

        return result;
    }

    // ================= Branchless comparison =================

    [Benchmark]
    public byte Byte_Position15_Branchless()
    {
        SetupByteStack(15, in TestValue);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.TryPopSmallIndex(out uint position);
        ref byte bytes = ref stack.PopBytesByRef();

        // Branchless: use conditional move pattern
        // mask = position < 32 ? 0xFF : 0x00
        uint mask = (uint)-(int)(31 - position >> 31); // 0xFFFFFFFF if position < 32, else 0
        byte result = (byte)(Unsafe.Add(ref bytes, (nuint)(position & 31)) & mask);
        stack.PushByte<OffFlag>(result);

        return result;
    }

    [Benchmark]
    public byte Byte_Position32_Branchless()
    {
        SetupByteStack(32, in TestValue);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.TryPopSmallIndex(out uint position);
        ref byte bytes = ref stack.PopBytesByRef();

        uint mask = (uint)-(int)(31 - position >> 31);
        byte result = (byte)(Unsafe.Add(ref bytes, (nuint)(position & 31)) & mask);
        stack.PushByte<OffFlag>(result);

        return result;
    }
}
