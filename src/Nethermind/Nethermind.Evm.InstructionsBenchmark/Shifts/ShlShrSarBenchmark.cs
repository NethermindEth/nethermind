// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Evm.InstructionsBenchmark.Helpers;
using Nethermind.Int256;

namespace Nethermind.Evm.InstructionsBenchmark.Shifts;

/// <summary>
/// Benchmarks for SHL, SHR, SAR shift operations.
/// Run: dotnet run -c Release --filter "*ShlShrSarBenchmark*"
/// </summary>
[DisassemblyDiagnoser(maxDepth: 3, exportGithubMarkdown: true, exportHtml: true)]
public class ShlShrSarBenchmark
{
    private byte[] _stackBuffer = null!;
    private byte[] _codeBuffer = null!;

    // Test values
    private static readonly UInt256 Value = UInt256.Parse("0x123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0");
    private static readonly UInt256 SmallValue = 0xFFFFFFFFUL;
    private static readonly UInt256 SignedNegative = UInt256.MaxValue - 100; // Large value (negative when signed)

    [GlobalSetup]
    public void Setup()
    {
        _stackBuffer = BenchmarkHelpers.CreateStackBuffer();
        _codeBuffer = new byte[1];
    }

    private void SetupShiftStack(uint shiftAmount, in UInt256 value)
    {
        BenchmarkHelpers.ClearStackSlot(_stackBuffer, 0);
        BenchmarkHelpers.WriteStackSlot(_stackBuffer, 0, value);
        BenchmarkHelpers.ClearStackSlot(_stackBuffer, 1);
        BenchmarkHelpers.WriteStackSlotU64(_stackBuffer, 1, shiftAmount);
    }

    // ================= SHL Benchmarks =================

    [Benchmark(Baseline = true)]
    public UInt256 Shl_By1()
    {
        SetupShiftStack(1, in Value);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 shiftAmount, out UInt256 value);
        UInt256 result = value << (int)shiftAmount.u0;
        stack.PushUInt256<OffFlag>(in result);

        return result;
    }

    [Benchmark]
    public UInt256 Shl_By64()
    {
        SetupShiftStack(64, in Value);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 shiftAmount, out UInt256 value);
        UInt256 result = value << (int)shiftAmount.u0;
        stack.PushUInt256<OffFlag>(in result);

        return result;
    }

    [Benchmark]
    public UInt256 Shl_By128()
    {
        SetupShiftStack(128, in Value);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 shiftAmount, out UInt256 value);
        UInt256 result = value << (int)shiftAmount.u0;
        stack.PushUInt256<OffFlag>(in result);

        return result;
    }

    [Benchmark]
    public UInt256 Shl_By255()
    {
        SetupShiftStack(255, in Value);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 shiftAmount, out UInt256 value);
        UInt256 result = value << (int)shiftAmount.u0;
        stack.PushUInt256<OffFlag>(in result);

        return result;
    }

    // ================= SHR Benchmarks =================

    [Benchmark]
    public UInt256 Shr_By1()
    {
        SetupShiftStack(1, in Value);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 shiftAmount, out UInt256 value);
        UInt256 result = value >> (int)shiftAmount.u0;
        stack.PushUInt256<OffFlag>(in result);

        return result;
    }

    [Benchmark]
    public UInt256 Shr_By64()
    {
        SetupShiftStack(64, in Value);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 shiftAmount, out UInt256 value);
        UInt256 result = value >> (int)shiftAmount.u0;
        stack.PushUInt256<OffFlag>(in result);

        return result;
    }

    [Benchmark]
    public UInt256 Shr_By128()
    {
        SetupShiftStack(128, in Value);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 shiftAmount, out UInt256 value);
        UInt256 result = value >> (int)shiftAmount.u0;
        stack.PushUInt256<OffFlag>(in result);

        return result;
    }

    // ================= SAR Benchmarks =================

    [Benchmark]
    public UInt256 Sar_Positive_By1()
    {
        SetupShiftStack(1, in SmallValue);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 shiftAmount, out UInt256 value);
        Unsafe.As<UInt256, Int256.Int256>(ref value).RightShift((int)shiftAmount.u0, out Int256.Int256 signedResult);
        UInt256 result = Unsafe.As<Int256.Int256, UInt256>(ref signedResult);
        stack.PushUInt256<OffFlag>(in result);

        return result;
    }

    [Benchmark]
    public UInt256 Sar_Negative_By1()
    {
        SetupShiftStack(1, in SignedNegative);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 shiftAmount, out UInt256 value);
        Unsafe.As<UInt256, Int256.Int256>(ref value).RightShift((int)shiftAmount.u0, out Int256.Int256 signedResult);
        UInt256 result = Unsafe.As<Int256.Int256, UInt256>(ref signedResult);
        stack.PushUInt256<OffFlag>(in result);

        return result;
    }

    [Benchmark]
    public UInt256 Sar_Negative_By64()
    {
        SetupShiftStack(64, in SignedNegative);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 shiftAmount, out UInt256 value);
        Unsafe.As<UInt256, Int256.Int256>(ref value).RightShift((int)shiftAmount.u0, out Int256.Int256 signedResult);
        UInt256 result = Unsafe.As<Int256.Int256, UInt256>(ref signedResult);
        stack.PushUInt256<OffFlag>(in result);

        return result;
    }

    [Benchmark]
    public UInt256 Sar_Negative_By255()
    {
        SetupShiftStack(255, in SignedNegative);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 shiftAmount, out UInt256 value);
        Unsafe.As<UInt256, Int256.Int256>(ref value).RightShift((int)shiftAmount.u0, out Int256.Int256 signedResult);
        UInt256 result = Unsafe.As<Int256.Int256, UInt256>(ref signedResult);
        stack.PushUInt256<OffFlag>(in result);

        return result;
    }

    // ================= Direct Operation (without stack) =================

    [Benchmark]
    public UInt256 Shl_DirectOperation()
    {
        EvmInstructions.OpShl.Operation(64, in Value, out UInt256 result);
        return result;
    }

    [Benchmark]
    public UInt256 Shr_DirectOperation()
    {
        EvmInstructions.OpShr.Operation(64, in Value, out UInt256 result);
        return result;
    }
}
