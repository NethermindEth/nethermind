// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Evm.InstructionsBenchmark.Helpers;
using Nethermind.Int256;

namespace Nethermind.Evm.InstructionsBenchmark.Arithmetic;

/// <summary>
/// Benchmarks for DIV, SDIV, MOD, SMOD operations.
/// Run: dotnet run -c Release --filter "*DivModBenchmark*"
/// </summary>
[DisassemblyDiagnoser(maxDepth: 3, exportGithubMarkdown: true, exportHtml: true)]
public class DivModBenchmark
{
    private byte[] _stackBuffer = null!;
    private byte[] _codeBuffer = null!;

    // Test values
    private static readonly UInt256 Dividend = UInt256.Parse("0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF00000000000000000000000000000000");
    private static readonly UInt256 SmallDivisor = 12345;
    private static readonly UInt256 LargeDivisor = UInt256.Parse("0x123456789ABCDEF0");
    private static readonly UInt256 Zero = UInt256.Zero;

    [GlobalSetup]
    public void Setup()
    {
        _stackBuffer = BenchmarkHelpers.CreateStackBuffer();
        _codeBuffer = new byte[1];
    }

    private void SetupStack(in UInt256 a, in UInt256 b)
    {
        BenchmarkHelpers.WriteStackSlot(_stackBuffer, 0, in b);
        BenchmarkHelpers.WriteStackSlot(_stackBuffer, 1, in a);
    }

    [Benchmark(Baseline = true)]
    public UInt256 Div_SmallDivisor()
    {
        SetupStack(in Dividend, in SmallDivisor);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 a, out UInt256 b);

        UInt256 result;
        if (b.IsZero)
            result = default;
        else
            UInt256.Divide(in a, in b, out result);

        stack.PushUInt256<OffFlag>(in result);
        return result;
    }

    [Benchmark]
    public UInt256 Div_LargeDivisor()
    {
        SetupStack(in Dividend, in LargeDivisor);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 a, out UInt256 b);

        UInt256 result;
        if (b.IsZero)
            result = default;
        else
            UInt256.Divide(in a, in b, out result);

        stack.PushUInt256<OffFlag>(in result);
        return result;
    }

    [Benchmark]
    public UInt256 Div_ByZero()
    {
        SetupStack(in Dividend, in Zero);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 a, out UInt256 b);

        UInt256 result;
        if (b.IsZero)
            result = default;
        else
            UInt256.Divide(in a, in b, out result);

        stack.PushUInt256<OffFlag>(in result);
        return result;
    }

    [Benchmark]
    public UInt256 Mod_SmallDivisor()
    {
        SetupStack(in Dividend, in SmallDivisor);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 a, out UInt256 b);

        UInt256 result;
        if (b.IsZeroOrOne)
            result = default;
        else
            UInt256.Mod(in a, in b, out result);

        stack.PushUInt256<OffFlag>(in result);
        return result;
    }

    [Benchmark]
    public UInt256 Mod_LargeDivisor()
    {
        SetupStack(in Dividend, in LargeDivisor);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 a, out UInt256 b);

        UInt256 result;
        if (b.IsZeroOrOne)
            result = default;
        else
            UInt256.Mod(in a, in b, out result);

        stack.PushUInt256<OffFlag>(in result);
        return result;
    }
}
