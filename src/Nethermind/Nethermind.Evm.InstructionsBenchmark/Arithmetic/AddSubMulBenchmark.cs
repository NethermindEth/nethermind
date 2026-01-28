// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Evm.InstructionsBenchmark.Helpers;
using Nethermind.Int256;

namespace Nethermind.Evm.InstructionsBenchmark.Arithmetic;

/// <summary>
/// Benchmarks for ADD, SUB, MUL operations - testing stack pop/push patterns.
/// Run: dotnet run -c Release --filter "*AddSubMulBenchmark*"
/// </summary>
[DisassemblyDiagnoser(maxDepth: 3, exportGithubMarkdown: true, exportHtml: true)]
public class AddSubMulBenchmark
{
    private byte[] _stackBuffer = null!;
    private byte[] _codeBuffer = null!;

    // Test values
    private static readonly UInt256 SmallA = 12345;
    private static readonly UInt256 SmallB = 67890;
    private static readonly UInt256 LargeA = UInt256.Parse("0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF");
    private static readonly UInt256 LargeB = UInt256.Parse("0x123456789ABCDEF0123456789ABCDEF0");

    [GlobalSetup]
    public void Setup()
    {
        _stackBuffer = BenchmarkHelpers.CreateStackBuffer();
        _codeBuffer = new byte[1];
    }

    private void SetupStack(in UInt256 a, in UInt256 b)
    {
        // Stack slot 0: b (deeper)
        // Stack slot 1: a (top)
        BenchmarkHelpers.WriteStackSlot(_stackBuffer, 0, in b);
        BenchmarkHelpers.WriteStackSlot(_stackBuffer, 1, in a);
    }

    [Benchmark(Baseline = true)]
    public UInt256 Add_SmallValues()
    {
        SetupStack(in SmallA, in SmallB);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 a, out UInt256 b);
        UInt256.Add(in a, in b, out UInt256 result);
        stack.PushUInt256<OffFlag>(in result);

        return result;
    }

    [Benchmark]
    public UInt256 Add_LargeValues()
    {
        SetupStack(in LargeA, in LargeB);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 a, out UInt256 b);
        UInt256.Add(in a, in b, out UInt256 result);
        stack.PushUInt256<OffFlag>(in result);

        return result;
    }

    [Benchmark]
    public UInt256 Sub_SmallValues()
    {
        SetupStack(in SmallA, in SmallB);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 a, out UInt256 b);
        UInt256.Subtract(in a, in b, out UInt256 result);
        stack.PushUInt256<OffFlag>(in result);

        return result;
    }

    [Benchmark]
    public UInt256 Sub_LargeValues()
    {
        SetupStack(in LargeA, in LargeB);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 a, out UInt256 b);
        UInt256.Subtract(in a, in b, out UInt256 result);
        stack.PushUInt256<OffFlag>(in result);

        return result;
    }

    [Benchmark]
    public UInt256 Mul_SmallValues()
    {
        SetupStack(in SmallA, in SmallB);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 a, out UInt256 b);
        UInt256.Multiply(in a, in b, out UInt256 result);
        stack.PushUInt256<OffFlag>(in result);

        return result;
    }

    [Benchmark]
    public UInt256 Mul_LargeValues()
    {
        SetupStack(in LargeA, in LargeB);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 a, out UInt256 b);
        UInt256.Multiply(in a, in b, out UInt256 result);
        stack.PushUInt256<OffFlag>(in result);

        return result;
    }

    /// <summary>
    /// Benchmark of just the pop operation for 2 UInt256 values.
    /// </summary>
    [Benchmark]
    public (UInt256, UInt256) PopTwo()
    {
        SetupStack(in LargeA, in LargeB);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 a, out UInt256 b);
        return (a, b);
    }

    /// <summary>
    /// Benchmark of just the push operation for UInt256.
    /// </summary>
    [Benchmark]
    public int PushOne()
    {
        var stack = new EvmStack(0, ref _stackBuffer[0], _codeBuffer);
        stack.PushUInt256<OffFlag>(in LargeA);
        return stack.Head;
    }
}
