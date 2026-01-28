// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Evm.InstructionsBenchmark.Helpers;
using Nethermind.Int256;

namespace Nethermind.Evm.InstructionsBenchmark.Comparison;

using Word = Vector256<byte>;

/// <summary>
/// Benchmarks for LT, GT, SLT, SGT, EQ comparison operations.
/// Run: dotnet run -c Release --filter "*LtGtEqBenchmark*"
/// </summary>
[DisassemblyDiagnoser(maxDepth: 3, exportGithubMarkdown: true, exportHtml: true)]
public class LtGtEqBenchmark
{
    private byte[] _stackBuffer = null!;
    private byte[] _codeBuffer = null!;

    // Test values
    private static readonly UInt256 SmallA = 100;
    private static readonly UInt256 SmallB = 200;
    private static readonly UInt256 LargeA = UInt256.Parse("0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF");
    private static readonly UInt256 LargeB = UInt256.Parse("0x123456789ABCDEF0123456789ABCDEF0");
    private static readonly UInt256 Equal = UInt256.Parse("0xABCDEF0123456789");

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
    public UInt256 Lt_SmallValues()
    {
        SetupStack(in SmallA, in SmallB);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 a, out UInt256 b);
        UInt256 result = a < b ? UInt256.One : default;
        stack.PushUInt256<OffFlag>(in result);

        return result;
    }

    [Benchmark]
    public UInt256 Lt_LargeValues()
    {
        SetupStack(in LargeA, in LargeB);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 a, out UInt256 b);
        UInt256 result = a < b ? UInt256.One : default;
        stack.PushUInt256<OffFlag>(in result);

        return result;
    }

    [Benchmark]
    public UInt256 Gt_SmallValues()
    {
        SetupStack(in SmallA, in SmallB);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 a, out UInt256 b);
        UInt256 result = a > b ? UInt256.One : default;
        stack.PushUInt256<OffFlag>(in result);

        return result;
    }

    [Benchmark]
    public UInt256 Gt_LargeValues()
    {
        SetupStack(in LargeA, in LargeB);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 a, out UInt256 b);
        UInt256 result = a > b ? UInt256.One : default;
        stack.PushUInt256<OffFlag>(in result);

        return result;
    }

    [Benchmark]
    public UInt256 Eq_NotEqual()
    {
        SetupStack(in LargeA, in LargeB);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 a, out UInt256 b);
        UInt256 result = a == b ? UInt256.One : default;
        stack.PushUInt256<OffFlag>(in result);

        return result;
    }

    [Benchmark]
    public UInt256 Eq_Equal()
    {
        SetupStack(in Equal, in Equal);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 a, out UInt256 b);
        UInt256 result = a == b ? UInt256.One : default;
        stack.PushUInt256<OffFlag>(in result);

        return result;
    }

    /// <summary>
    /// Test the vector-based EQ operation (as used in InstructionBitwise).
    /// </summary>
    [Benchmark]
    public Word Eq_VectorBased()
    {
        SetupStack(in LargeA, in LargeB);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        ref byte bytesRef = ref stack.PopBytesByRef();
        Word aVec = Unsafe.ReadUnaligned<Word>(ref bytesRef);

        bytesRef = ref stack.PeekBytesByRef();
        Word bVec = Unsafe.ReadUnaligned<Word>(ref bytesRef);

        Word result = Vector256.EqualsAll(aVec, bVec) ? EvmInstructions.OpBitwiseEq.One : default;
        Unsafe.WriteUnaligned(ref bytesRef, result);

        return result;
    }

    [Benchmark]
    public UInt256 Slt_SmallValues()
    {
        SetupStack(in SmallA, in SmallB);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 a, out UInt256 b);
        // Signed comparison using Int256 - branchless version
        int cmp = Unsafe.As<UInt256, Int256.Int256>(ref a)
            .CompareTo(Unsafe.As<UInt256, Int256.Int256>(ref b));
        // Extract sign bit branchlessly
        UInt256 result = new UInt256((uint)cmp >> 31, 0UL, 0UL, 0UL);
        stack.PushUInt256<OffFlag>(in result);

        return result;
    }

    [Benchmark]
    public UInt256 Sgt_SmallValues()
    {
        SetupStack(in SmallA, in SmallB);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        stack.PopUInt256(out UInt256 a, out UInt256 b);
        // Signed comparison using Int256 - branchless version
        int cmp = Unsafe.As<UInt256, Int256.Int256>(ref a)
            .CompareTo(Unsafe.As<UInt256, Int256.Int256>(ref b));
        // For cmp > 0: negate then extract sign bit
        UInt256 result = new UInt256((uint)(-cmp) >> 31, 0UL, 0UL, 0UL);
        stack.PushUInt256<OffFlag>(in result);

        return result;
    }

    // ================= Branchless Direct Operations =================

    [Benchmark]
    public UInt256 Lt_Branchless_Direct()
    {
        EvmInstructions.OpLt.Operation(in SmallA, in SmallB, out UInt256 result);
        return result;
    }

    [Benchmark]
    public UInt256 Gt_Branchless_Direct()
    {
        EvmInstructions.OpGt.Operation(in SmallA, in SmallB, out UInt256 result);
        return result;
    }

    [Benchmark]
    public UInt256 Slt_Branchless_Direct()
    {
        EvmInstructions.OpSLt.Operation(in SmallA, in SmallB, out UInt256 result);
        return result;
    }

    [Benchmark]
    public UInt256 Sgt_Branchless_Direct()
    {
        EvmInstructions.OpSGt.Operation(in SmallA, in SmallB, out UInt256 result);
        return result;
    }
}
