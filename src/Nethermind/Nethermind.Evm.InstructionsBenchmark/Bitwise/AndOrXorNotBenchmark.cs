// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.InstructionsBenchmark.Helpers;
using Nethermind.Int256;

namespace Nethermind.Evm.InstructionsBenchmark.Bitwise;

using Word = Vector256<byte>;

/// <summary>
/// Benchmarks for AND, OR, XOR, NOT bitwise operations.
/// Run: dotnet run -c Release --filter "*AndOrXorNotBenchmark*"
/// </summary>
[DisassemblyDiagnoser(maxDepth: 3, exportGithubMarkdown: true, exportHtml: true)]
public class AndOrXorNotBenchmark
{
    private byte[] _stackBuffer = null!;
    private byte[] _codeBuffer = null!;

    // Test values - patterns that exercise different bit arrangements
    private static readonly UInt256 Pattern1 = UInt256.Parse("0xAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
    private static readonly UInt256 Pattern2 = UInt256.Parse("0x5555555555555555555555555555555555555555555555555555555555555555");
    private static readonly UInt256 AllOnes = UInt256.MaxValue;
    private static readonly UInt256 AllZeros = UInt256.Zero;

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

    private void SetupStackSingle(in UInt256 a)
    {
        BenchmarkHelpers.WriteStackSlot(_stackBuffer, 0, in a);
    }

    /// <summary>
    /// Bitwise AND using vector operations (current implementation).
    /// </summary>
    [Benchmark(Baseline = true)]
    public Word And_Vector()
    {
        SetupStack(in Pattern1, in Pattern2);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        ref byte bytesRef = ref stack.PopBytesByRef();
        Word aVec = Unsafe.ReadUnaligned<Word>(ref bytesRef);

        bytesRef = ref stack.PeekBytesByRef();
        Word bVec = Unsafe.ReadUnaligned<Word>(ref bytesRef);

        Word result = Vector256.BitwiseAnd(aVec, bVec);
        Unsafe.WriteUnaligned(ref bytesRef, result);

        return result;
    }

    [Benchmark]
    public Word Or_Vector()
    {
        SetupStack(in Pattern1, in Pattern2);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        ref byte bytesRef = ref stack.PopBytesByRef();
        Word aVec = Unsafe.ReadUnaligned<Word>(ref bytesRef);

        bytesRef = ref stack.PeekBytesByRef();
        Word bVec = Unsafe.ReadUnaligned<Word>(ref bytesRef);

        Word result = Vector256.BitwiseOr(aVec, bVec);
        Unsafe.WriteUnaligned(ref bytesRef, result);

        return result;
    }

    [Benchmark]
    public Word Xor_Vector()
    {
        SetupStack(in Pattern1, in Pattern2);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        ref byte bytesRef = ref stack.PopBytesByRef();
        Word aVec = Unsafe.ReadUnaligned<Word>(ref bytesRef);

        bytesRef = ref stack.PeekBytesByRef();
        Word bVec = Unsafe.ReadUnaligned<Word>(ref bytesRef);

        Word result = Vector256.Xor(aVec, bVec);
        Unsafe.WriteUnaligned(ref bytesRef, result);

        return result;
    }

    /// <summary>
    /// NOT operation using Vector256.OnesComplement.
    /// </summary>
    [Benchmark]
    public Word Not_Vector()
    {
        SetupStackSingle(in Pattern1);
        var stack = new EvmStack(1, ref _stackBuffer[0], _codeBuffer);

        ref byte bytesRef = ref stack.PeekBytesByRef();
        Word value = Unsafe.ReadUnaligned<Word>(ref bytesRef);
        Word result = Vector256.OnesComplement(value);
        Unsafe.WriteUnaligned(ref bytesRef, result);

        return result;
    }

    [Benchmark]
    public Word Not_AllOnes()
    {
        SetupStackSingle(in AllOnes);
        var stack = new EvmStack(1, ref _stackBuffer[0], _codeBuffer);

        ref byte bytesRef = ref stack.PeekBytesByRef();
        Word value = Unsafe.ReadUnaligned<Word>(ref bytesRef);
        Word result = Vector256.OnesComplement(value);
        Unsafe.WriteUnaligned(ref bytesRef, result);

        return result;
    }

    [Benchmark]
    public Word Not_AllZeros()
    {
        SetupStackSingle(in AllZeros);
        var stack = new EvmStack(1, ref _stackBuffer[0], _codeBuffer);

        ref byte bytesRef = ref stack.PeekBytesByRef();
        Word value = Unsafe.ReadUnaligned<Word>(ref bytesRef);
        Word result = Vector256.OnesComplement(value);
        Unsafe.WriteUnaligned(ref bytesRef, result);

        return result;
    }

    /// <summary>
    /// ISZERO operation using vector comparison.
    /// </summary>
    [Benchmark]
    public Word IsZero_NonZero()
    {
        SetupStackSingle(in Pattern1);
        var stack = new EvmStack(1, ref _stackBuffer[0], _codeBuffer);

        ref byte bytesRef = ref stack.PeekBytesByRef();
        Word value = Unsafe.ReadUnaligned<Word>(ref bytesRef);
        Word result = Vector256.EqualsAll(value, default) ? EvmInstructions.OpBitwiseEq.One : default;
        Unsafe.WriteUnaligned(ref bytesRef, result);

        return result;
    }

    [Benchmark]
    public Word IsZero_Zero()
    {
        SetupStackSingle(in AllZeros);
        var stack = new EvmStack(1, ref _stackBuffer[0], _codeBuffer);

        ref byte bytesRef = ref stack.PeekBytesByRef();
        Word value = Unsafe.ReadUnaligned<Word>(ref bytesRef);
        Word result = Vector256.EqualsAll(value, default) ? EvmInstructions.OpBitwiseEq.One : default;
        Unsafe.WriteUnaligned(ref bytesRef, result);

        return result;
    }

    /// <summary>
    /// AND using PopPeekBytesByRef (single bounds check).
    /// </summary>
    [Benchmark]
    public Word And_PopPeek()
    {
        SetupStack(in Pattern1, in Pattern2);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        ref byte top = ref stack.PopPeekBytesByRef();
        ref byte popped = ref Unsafe.Add(ref top, EvmStack.WordSize);
        Word aVec = Unsafe.ReadUnaligned<Word>(ref popped);
        Word bVec = Unsafe.ReadUnaligned<Word>(ref top);

        Word result = Vector256.BitwiseAnd(aVec, bVec);
        Unsafe.WriteUnaligned(ref top, result);

        return result;
    }

    /// <summary>
    /// OR using PopPeekBytesByRef (single bounds check).
    /// </summary>
    [Benchmark]
    public Word Or_PopPeek()
    {
        SetupStack(in Pattern1, in Pattern2);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        ref byte top = ref stack.PopPeekBytesByRef();
        ref byte popped = ref Unsafe.Add(ref top, EvmStack.WordSize);
        Word aVec = Unsafe.ReadUnaligned<Word>(ref popped);
        Word bVec = Unsafe.ReadUnaligned<Word>(ref top);

        Word result = Vector256.BitwiseOr(aVec, bVec);
        Unsafe.WriteUnaligned(ref top, result);

        return result;
    }

    /// <summary>
    /// XOR using PopPeekBytesByRef (single bounds check).
    /// </summary>
    [Benchmark]
    public Word Xor_PopPeek()
    {
        SetupStack(in Pattern1, in Pattern2);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        ref byte top = ref stack.PopPeekBytesByRef();
        ref byte popped = ref Unsafe.Add(ref top, EvmStack.WordSize);
        Word aVec = Unsafe.ReadUnaligned<Word>(ref popped);
        Word bVec = Unsafe.ReadUnaligned<Word>(ref top);

        Word result = Vector256.Xor(aVec, bVec);
        Unsafe.WriteUnaligned(ref top, result);

        return result;
    }

    /// <summary>
    /// Actual InstructionBitwise AND call.
    /// </summary>
    [Benchmark]
    public OpcodeResult And_Instruction()
    {
        SetupStack(in Pattern1, in Pattern2);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);
        var gas = EthereumGasPolicy.FromLong(100000);

        return EvmInstructions.InstructionBitwise<EthereumGasPolicy, EvmInstructions.OpBitwiseAnd>(null!, ref stack, ref gas, 0);
    }

    /// <summary>
    /// Actual InstructionBitwise XOR call.
    /// </summary>
    [Benchmark]
    public OpcodeResult Xor_Instruction()
    {
        SetupStack(in Pattern1, in Pattern2);
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);
        var gas = EthereumGasPolicy.FromLong(100000);

        return EvmInstructions.InstructionBitwise<EthereumGasPolicy, EvmInstructions.OpBitwiseXor>(null!, ref stack, ref gas, 0);
    }
}
