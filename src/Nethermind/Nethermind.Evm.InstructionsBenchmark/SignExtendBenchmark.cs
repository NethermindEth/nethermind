// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using BenchmarkDotNet.Attributes;

namespace Nethermind.Evm.InstructionsBenchmark;

[DisassemblyDiagnoser(maxDepth: 3, exportGithubMarkdown: true, exportHtml: true)]
[MemoryDiagnoser]
public class SignExtendBenchmark
{
    private byte[] _buffer = null!;

    [Params(4, 12, 24, 31)]
    public int Position { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _buffer = new byte[32];
        for (int i = 0; i < 32; i++)
        {
            _buffer[i] = (byte)(i + 0x40);
        }
        // Set sign bit at position
        _buffer[31 - Position] = 0x80;
    }

    [Benchmark(Baseline = true)]
    public void ScalarUnrolled()
    {
        ref byte slot = ref _buffer[0];
        int position = 31 - Position;
        sbyte sign = (sbyte)Unsafe.Add(ref slot, position);
        SignExtendScalarUnrolled(ref slot, position, sign);
    }

    [Benchmark]
    public void ScalarUnrolledWideRead()
    {
        ref byte slot = ref _buffer[0];
        int position = 31 - Position;
        SignExtendScalarUnrolledWideRead(ref slot, position);
    }

    [Benchmark]
    public void ScalarSimpleLoop()
    {
        ref byte slot = ref _buffer[0];
        int position = 31 - Position;
        sbyte sign = (sbyte)Unsafe.Add(ref slot, position);
        SignExtendScalarSimple(ref slot, position, sign);
    }

    [Benchmark]
    public void Avx2Vectorized()
    {
        if (!Avx2.IsSupported) return;
        ref byte slot = ref _buffer[0];
        int position = 31 - Position;
        sbyte sign = (sbyte)Unsafe.Add(ref slot, position);
        SignExtendAvx2(ref slot, position, sign);
    }

    [Benchmark]
    public void Avx2VectorizedExtract()
    {
        if (!Avx2.IsSupported) return;
        ref byte slot = ref _buffer[0];
        int position = 31 - Position;
        SignExtendAvx2Extract(ref slot, position);
    }

    [Benchmark]
    public void HybridOptimized()
    {
        ref byte slot = ref _buffer[0];
        int position = 31 - Position;
        sbyte sign = (sbyte)Unsafe.Add(ref slot, position);
        SignExtendHybrid(ref slot, position, sign);
    }

    [Benchmark]
    public void HybridWideRead()
    {
        ref byte slot = ref _buffer[0];
        int position = 31 - Position;
        SignExtendHybridWideRead(ref slot, position);
    }

    /// <summary>
    /// Unrolled version - current implementation after optimization
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SignExtendScalarUnrolled(ref byte slot, int position, sbyte sign)
    {
        byte fill = (byte)(sign >> 7);
        ulong fillWord = fill == 0 ? 0UL : ulong.MaxValue;

        int i = 0;
        while (i + 8 <= position)
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref slot, i), fillWord);
            i += 8;
        }
        if (i + 4 <= position)
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref slot, i), (uint)fillWord);
            i += 4;
        }
        if (i + 2 <= position)
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref slot, i), (ushort)fillWord);
            i += 2;
        }
        if (i < position)
        {
            Unsafe.Add(ref slot, i) = fill;
        }
    }

    /// <summary>
    /// Unrolled version with wide read - extracts sign from ulong instead of narrow byte read
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SignExtendScalarUnrolledWideRead(ref byte slot, int position)
    {
        if (position == 0) return;

        // Read sign byte via wider load to avoid narrow read penalty
        int ulongOffset = position & ~7;
        int byteOffset = position & 7;
        ulong word = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref slot, ulongOffset));
        byte fill = (byte)((sbyte)(word >> (byteOffset * 8)) >> 7);
        ulong fillWord = fill == 0 ? 0UL : ulong.MaxValue;

        int i = 0;
        while (i + 8 <= position)
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref slot, i), fillWord);
            i += 8;
        }
        if (i + 4 <= position)
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref slot, i), (uint)fillWord);
            i += 4;
        }
        if (i + 2 <= position)
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref slot, i), (ushort)fillWord);
            i += 2;
        }
        if (i < position)
        {
            Unsafe.Add(ref slot, i) = fill;
        }
    }

    /// <summary>
    /// Simple loop version - original implementation
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SignExtendScalarSimple(ref byte slot, int position, sbyte sign)
    {
        byte fill = (byte)(sign >> 7);
        for (int i = 0; i < position; i++)
        {
            Unsafe.Add(ref slot, i) = fill;
        }
    }

    /// <summary>
    /// AVX2 vectorized version
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SignExtendAvx2(ref byte slot, int position, sbyte sign)
    {
        Vector256<byte> value = Unsafe.As<byte, Vector256<byte>>(ref slot);
        Vector256<byte> indices = Vector256.Create(
            (byte)0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
            16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31);
        Vector256<byte> posVec = Vector256.Create((byte)position);
        Vector256<byte> mask = Avx2.CompareGreaterThan(posVec.AsSByte(), indices.AsSByte()).AsByte();
        Vector256<byte> fill = Vector256.Create((byte)(sign >> 7));
        Vector256<byte> result = Avx2.BlendVariable(value, fill, mask);
        Unsafe.As<byte, Vector256<byte>>(ref slot) = result;
    }

    /// <summary>
    /// AVX2 vectorized version - extracts sign from loaded vector instead of separate byte read
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SignExtendAvx2Extract(ref byte slot, int position)
    {
        Vector256<byte> value = Unsafe.As<byte, Vector256<byte>>(ref slot);
        // Extract sign from already-loaded vector (avoids separate narrow memory read)
        sbyte sign = (sbyte)value.GetElement(position);
        Vector256<byte> indices = Vector256.Create(
            (byte)0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
            16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31);
        Vector256<byte> posVec = Vector256.Create((byte)position);
        Vector256<byte> mask = Avx2.CompareGreaterThan(posVec.AsSByte(), indices.AsSByte()).AsByte();
        Vector256<byte> fill = Vector256.Create((byte)(sign >> 7));
        Vector256<byte> result = Avx2.BlendVariable(value, fill, mask);
        Unsafe.As<byte, Vector256<byte>>(ref slot) = result;
    }

    /// <summary>
    /// Hybrid approach - uses AVX2 for large fills (8+ bytes), unrolled scalar for small fills
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SignExtendHybrid(ref byte slot, int position, sbyte sign)
    {
        // Use AVX2 for larger fills (8+ bytes), scalar for smaller fills
        if (Avx2.IsSupported && position >= 8)
        {
            Vector256<byte> value = Unsafe.As<byte, Vector256<byte>>(ref slot);
            Vector256<byte> indices = Vector256.Create(
                (byte)0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
                16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31);
            Vector256<byte> posVec = Vector256.Create((byte)position);
            Vector256<byte> mask = Avx2.CompareGreaterThan(posVec.AsSByte(), indices.AsSByte()).AsByte();
            Vector256<byte> fill = Vector256.Create((byte)(sign >> 7));
            Vector256<byte> result = Avx2.BlendVariable(value, fill, mask);
            Unsafe.As<byte, Vector256<byte>>(ref slot) = result;
        }
        else
        {
            // Early exit for no-op
            if (position == 0) return;

            // Unrolled scalar for small fills
            byte fill = (byte)(sign >> 7);
            ulong fillWord = fill == 0 ? 0UL : ulong.MaxValue;

            int i = 0;
            if (i + 4 <= position)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref slot, i), (uint)fillWord);
                i += 4;
            }
            if (i + 2 <= position)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref slot, i), (ushort)fillWord);
                i += 2;
            }
            if (i < position)
            {
                Unsafe.Add(ref slot, i) = fill;
            }
        }
    }

    /// <summary>
    /// Hybrid approach with wide reads - AVX2 extracts from vector, scalar reads from ulong
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SignExtendHybridWideRead(ref byte slot, int position)
    {
        // Use AVX2 for larger fills (8+ bytes), scalar for smaller fills
        if (Avx2.IsSupported && position >= 8)
        {
            Vector256<byte> value = Unsafe.As<byte, Vector256<byte>>(ref slot);
            // Extract sign from already-loaded vector
            sbyte sign = (sbyte)value.GetElement(position);
            Vector256<byte> indices = Vector256.Create(
                (byte)0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
                16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31);
            Vector256<byte> posVec = Vector256.Create((byte)position);
            Vector256<byte> mask = Avx2.CompareGreaterThan(posVec.AsSByte(), indices.AsSByte()).AsByte();
            Vector256<byte> fill = Vector256.Create((byte)(sign >> 7));
            Vector256<byte> result = Avx2.BlendVariable(value, fill, mask);
            Unsafe.As<byte, Vector256<byte>>(ref slot) = result;
        }
        else
        {
            // Early exit for no-op
            if (position == 0) return;

            // Read sign byte via wider load
            int ulongOffset = position & ~7;
            int byteOffset = position & 7;
            ulong word = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref slot, ulongOffset));
            byte fill = (byte)((sbyte)(word >> (byteOffset * 8)) >> 7);
            ulong fillWord = fill == 0 ? 0UL : ulong.MaxValue;

            int i = 0;
            if (i + 4 <= position)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref slot, i), (uint)fillWord);
                i += 4;
            }
            if (i + 2 <= position)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref slot, i), (ushort)fillWord);
                i += 2;
            }
            if (i < position)
            {
                Unsafe.Add(ref slot, i) = fill;
            }
        }
    }
}
