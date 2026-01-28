// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Evm.InstructionsBenchmark.Helpers;
using Nethermind.Int256;

namespace Nethermind.Evm.InstructionsBenchmark.Stack;

/// <summary>
/// Benchmarks for POP and various pop operations.
/// Run: dotnet run -c Release --filter "*PopBenchmark*"
/// </summary>
[DisassemblyDiagnoser(maxDepth: 3, exportGithubMarkdown: true, exportHtml: true)]
public class PopBenchmark
{
    private byte[] _stackBuffer = null!;
    private byte[] _codeBuffer = null!;

    private static readonly UInt256 TestValue = UInt256.Parse("0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF00000000000000000000000000000000");

    [GlobalSetup]
    public void Setup()
    {
        _stackBuffer = BenchmarkHelpers.CreateStackBuffer();
        _codeBuffer = new byte[1];

        // Pre-fill multiple stack slots for multi-pop tests
        for (int i = 0; i < 10; i++)
        {
            BenchmarkHelpers.WriteStackSlot(_stackBuffer, i, in TestValue);
        }
    }

    /// <summary>
    /// Simple discard (POP opcode equivalent).
    /// </summary>
    [Benchmark(Baseline = true)]
    public bool PopLimbo()
    {
        var stack = new EvmStack(5, ref _stackBuffer[0], _codeBuffer);
        return stack.PopLimbo();
    }

    /// <summary>
    /// Pop single UInt256.
    /// </summary>
    [Benchmark]
    public UInt256 PopUInt256_Single()
    {
        var stack = new EvmStack(1, ref _stackBuffer[0], _codeBuffer);
        stack.PopUInt256(out UInt256 result);
        return result;
    }

    /// <summary>
    /// Pop two UInt256 values (amortized bounds check).
    /// </summary>
    [Benchmark]
    public (UInt256, UInt256) PopUInt256_Two()
    {
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);
        stack.PopUInt256(out UInt256 a, out UInt256 b);
        return (a, b);
    }

    /// <summary>
    /// Pop three UInt256 values (amortized bounds check).
    /// </summary>
    [Benchmark]
    public (UInt256, UInt256, UInt256) PopUInt256_Three()
    {
        var stack = new EvmStack(3, ref _stackBuffer[0], _codeBuffer);
        stack.PopUInt256(out UInt256 a, out UInt256 b, out UInt256 c);
        return (a, b, c);
    }

    /// <summary>
    /// Pop four UInt256 values (amortized bounds check).
    /// </summary>
    [Benchmark]
    public (UInt256, UInt256, UInt256, UInt256) PopUInt256_Four()
    {
        var stack = new EvmStack(4, ref _stackBuffer[0], _codeBuffer);
        stack.PopUInt256(out UInt256 a, out UInt256 b, out UInt256 c, out UInt256 d);
        return (a, b, c, d);
    }

    /// <summary>
    /// Pop a single byte (position 31 of 32-byte word).
    /// </summary>
    [Benchmark]
    public int PopByte()
    {
        _stackBuffer[31] = 0x42; // Set test byte
        var stack = new EvmStack(1, ref _stackBuffer[0], _codeBuffer);
        return stack.PopByte();
    }

    /// <summary>
    /// Pop by reference (get ref to stack slot).
    /// </summary>
    [Benchmark]
    public int PopBytesByRef()
    {
        var stack = new EvmStack(1, ref _stackBuffer[0], _codeBuffer);
        ref byte result = ref stack.PopBytesByRef();
        return result; // Return first byte
    }

    /// <summary>
    /// TryPopSmallIndex for small index extraction (used by SIGNEXTEND, etc.)
    /// </summary>
    [Benchmark]
    public uint TryPopSmallIndex_Small()
    {
        BenchmarkHelpers.ClearStackSlot(_stackBuffer, 0);
        BenchmarkHelpers.WriteStackSlotByte(_stackBuffer, 0, 15); // Small value
        var stack = new EvmStack(1, ref _stackBuffer[0], _codeBuffer);
        stack.TryPopSmallIndex(out uint value);
        return value;
    }

    [Benchmark]
    public uint TryPopSmallIndex_Large()
    {
        // Set a large value that won't fit in small index
        BenchmarkHelpers.WriteStackSlot(_stackBuffer, 0, in TestValue);
        var stack = new EvmStack(1, ref _stackBuffer[0], _codeBuffer);
        stack.TryPopSmallIndex(out uint value);
        return value;
    }

    /// <summary>
    /// Peek at top without popping.
    /// </summary>
    [Benchmark]
    public byte PeekBytesByRef()
    {
        var stack = new EvmStack(1, ref _stackBuffer[0], _codeBuffer);
        ref byte result = ref stack.PeekBytesByRef();
        return result;
    }

    /// <summary>
    /// Check if top of stack is zero.
    /// </summary>
    [Benchmark]
    public bool PeekUInt256IsZero_NonZero()
    {
        var stack = new EvmStack(1, ref _stackBuffer[0], _codeBuffer);
        return stack.PeekUInt256IsZero();
    }

    [Benchmark]
    public bool PeekUInt256IsZero_Zero()
    {
        BenchmarkHelpers.ClearStackSlot(_stackBuffer, 0);
        var stack = new EvmStack(1, ref _stackBuffer[0], _codeBuffer);
        return stack.PeekUInt256IsZero();
    }
}
