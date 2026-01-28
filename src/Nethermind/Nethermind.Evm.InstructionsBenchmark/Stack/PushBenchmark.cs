// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Evm.InstructionsBenchmark.Helpers;
using Nethermind.Int256;

namespace Nethermind.Evm.InstructionsBenchmark.Stack;

/// <summary>
/// Benchmarks for PUSH operations (PUSH0-PUSH32).
/// Run: dotnet run -c Release --filter "*PushBenchmark*"
/// </summary>
[DisassemblyDiagnoser(maxDepth: 3, exportGithubMarkdown: true, exportHtml: true)]
public class PushBenchmark
{
    private byte[] _stackBuffer = null!;
    private byte[] _codeBuffer = null!;

    // Test data for various push sizes
    private static readonly byte[] Push1Data = [0x42];
    private static readonly byte[] Push2Data = [0x12, 0x34];
    private static readonly byte[] Push4Data = [0x12, 0x34, 0x56, 0x78];
    private static readonly byte[] Push8Data = [0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0];
    private static readonly byte[] Push16Data = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10];
    private static readonly byte[] Push32Data = [
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
        0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
        0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
        0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20
    ];

    private static readonly UInt256 TestUInt256 = UInt256.Parse("0x123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0");

    [GlobalSetup]
    public void Setup()
    {
        _stackBuffer = BenchmarkHelpers.CreateStackBuffer();
        _codeBuffer = new byte[64];
        // Copy test data to code buffer
        Push32Data.CopyTo(_codeBuffer.AsSpan());
    }

    // ================= PUSH0 =================

    [Benchmark(Baseline = true)]
    public EvmExceptionType Push0()
    {
        var stack = new EvmStack(0, ref _stackBuffer[0], _codeBuffer);
        return stack.PushZero<OffFlag>();
    }

    // ================= PUSH1 =================

    [Benchmark]
    public EvmExceptionType Push1()
    {
        var stack = new EvmStack(0, ref _stackBuffer[0], _codeBuffer);
        return stack.PushByte<OffFlag>(Push1Data[0]);
    }

    // ================= PUSH2 =================

    [Benchmark]
    public EvmExceptionType Push2()
    {
        var stack = new EvmStack(0, ref _stackBuffer[0], _codeBuffer);
        return stack.Push2Bytes<OffFlag>(ref MemoryMarshal.GetReference(Push2Data.AsSpan()));
    }

    // ================= PUSH4 =================

    [Benchmark]
    public EvmExceptionType Push4()
    {
        var stack = new EvmStack(0, ref _stackBuffer[0], _codeBuffer);
        return stack.Push4Bytes<OffFlag>(ref MemoryMarshal.GetReference(Push4Data.AsSpan()));
    }

    // ================= PUSH8 =================

    [Benchmark]
    public EvmExceptionType Push8()
    {
        var stack = new EvmStack(0, ref _stackBuffer[0], _codeBuffer);
        return stack.Push8Bytes<OffFlag>(ref MemoryMarshal.GetReference(Push8Data.AsSpan()));
    }

    // ================= PUSH16 =================

    [Benchmark]
    public EvmExceptionType Push16()
    {
        var stack = new EvmStack(0, ref _stackBuffer[0], _codeBuffer);
        return stack.Push16Bytes<OffFlag>(ref MemoryMarshal.GetReference(Push16Data.AsSpan()));
    }

    // ================= PUSH20 (Address) =================

    [Benchmark]
    public EvmExceptionType Push20()
    {
        var stack = new EvmStack(0, ref _stackBuffer[0], _codeBuffer);
        return stack.Push20Bytes<OffFlag>(ref MemoryMarshal.GetReference(Push32Data.AsSpan()));
    }

    // ================= PUSH32 =================

    [Benchmark]
    public EvmExceptionType Push32()
    {
        var stack = new EvmStack(0, ref _stackBuffer[0], _codeBuffer);
        return stack.Push32Bytes<OffFlag>(ref MemoryMarshal.GetReference(Push32Data.AsSpan()));
    }

    // ================= PushUInt256 =================

    [Benchmark]
    public EvmExceptionType PushUInt256_Value()
    {
        var stack = new EvmStack(0, ref _stackBuffer[0], _codeBuffer);
        return stack.PushUInt256<OffFlag>(in TestUInt256);
    }

    // ================= PushOne =================

    [Benchmark]
    public EvmExceptionType PushOne()
    {
        var stack = new EvmStack(0, ref _stackBuffer[0], _codeBuffer);
        return stack.PushOne<OffFlag>();
    }

    // ================= Push ulong =================

    [Benchmark]
    public EvmExceptionType PushUInt64()
    {
        var stack = new EvmStack(0, ref _stackBuffer[0], _codeBuffer);
        return stack.PushUInt64<OffFlag>(0x123456789ABCDEF0UL);
    }

    // ================= Push uint =================

    [Benchmark]
    public EvmExceptionType PushUInt32()
    {
        var stack = new EvmStack(0, ref _stackBuffer[0], _codeBuffer);
        return stack.PushUInt32<OffFlag>(0x12345678U);
    }

    // ================= Sequential pushes =================

    [Benchmark]
    public int Push_Multiple_Sequential()
    {
        var stack = new EvmStack(0, ref _stackBuffer[0], _codeBuffer);
        stack.PushZero<OffFlag>();
        stack.PushOne<OffFlag>();
        stack.PushUInt64<OffFlag>(42);
        stack.PushUInt256<OffFlag>(in TestUInt256);
        return stack.Head;
    }
}
