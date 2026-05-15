// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using BenchmarkDotNet.Attributes;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// Compares the scalar trailing loop used by <c>UniformKeySearch.FloorScan*</c> against
/// an AVX-512 "raw 64-byte load + masked compare" tail, for 16/32/64-bit LE keys.
/// </summary>
/// <remarks>
/// Each <c>FloorScan*</c> kernel processes whole vectors (32/16/8 keys per iteration
/// for keysize 2/4/8) and then calls a private <c>ScalarTail*</c> for the &lt;N
/// remaining lanes. This benchmark isolates that tail cost: <c>tail</c> is set below
/// one vector width so the main SIMD loop is skipped entirely and every lane is
/// handled by the tail path.
/// <para>
/// Scenario: search key &gt; every stored lane, so the kernel never early-exits and
/// must visit every lane — the worst case for the scalar tail and the cleanest
/// upper bound to compare against. Buffers are sized to a full <see cref="Vector512{T}"/>
/// and zero-padded past <c>tail</c>, so the masked variant issues one unmasked
/// 64-byte load (out-of-tail lanes read as zero, which never compare greater under
/// unsigned GT) and applies the lane mask to the result of <c>ExtractMostSignificantBits</c>.
/// This matches the semantics of a true <c>vmovdqu32 zmm{k}{z}</c> on this workload.
/// </para>
/// <para>
/// Search values are read from instance fields rather than typed-max constants so
/// the JIT cannot const-fold the <c>k &gt; search</c> compare in the scalar path
/// out of existence.
/// </para>
/// <para>
/// Three flavours are measured per width:
/// <list type="bullet">
///   <item><c>ScalarN</c>: the loop currently in <see cref="UniformKeySearch"/>.</item>
///   <item><c>MaskedN</c>: unmasked <see cref="Vector512.LoadUnsafe{T}(ref T)"/> over a
///         zero-padded buffer + masked extract of <c>ExtractMostSignificantBits</c>.</item>
///   <item><c>TrueMaskedN</c>: hardware masked load via
///         <see cref="Avx512F.MaskLoad(uint*, Vector512{uint}, Vector512{uint})"/> /
///         <see cref="Avx512BW.MaskLoad(ushort*, Vector512{ushort}, Vector512{ushort})"/>.
///         No padding required; lanes outside the mask never touch memory.</item>
/// </list>
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class UniformKeySearchTailBenchmark
{
    private const int Vector512Bytes = 64;

    // Lane-index vectors used to build the per-call mask: lane i is "in" iff i &lt; tail.
    private static readonly Vector512<ushort> LaneIdx16 = Vector512.Create(
        (ushort)0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31);
    private static readonly Vector512<uint> LaneIdx32 = Vector512.Create(
        0u, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15);
    private static readonly Vector512<ulong> LaneIdx64 = Vector512.Create(0ul, 1, 2, 3, 4, 5, 6, 7);

    private byte[] _keys2 = null!;
    private byte[] _keys4 = null!;
    private byte[] _keys8 = null!;

    private ushort _search16;
    private uint _search32;
    private ulong _search64;

    [GlobalSetup]
    public void Setup()
    {
        // POH-pinned so TrueMasked* can take a raw pointer with no per-call fixed cost.
        _keys2 = GC.AllocateUninitializedArray<byte>(Vector512Bytes, pinned: true);
        _keys4 = GC.AllocateUninitializedArray<byte>(Vector512Bytes, pinned: true);
        _keys8 = GC.AllocateUninitializedArray<byte>(Vector512Bytes, pinned: true);
        Array.Clear(_keys2);
        Array.Clear(_keys4);
        Array.Clear(_keys8);
        _search16 = ushort.MaxValue - 1;
        _search32 = uint.MaxValue - 1;
        _search64 = ulong.MaxValue - 1;
    }

    // =====================================================================================
    //  16-bit lanes (32 per Vector512). Tail range: 1..31.
    // =====================================================================================

    [Benchmark]
    [Arguments(1)]
    [Arguments(7)]
    [Arguments(15)]
    [Arguments(23)]
    [Arguments(31)]
    public int Scalar16(int tail)
    {
        ushort search = _search16;
        ref byte src = ref MemoryMarshal.GetReference(_keys2.AsSpan());
        for (int i = 0; i < tail; i++)
        {
            ushort k = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref src, (nint)(i * 2)));
            if (k > search) return i - 1;
        }
        return tail - 1;
    }

    [Benchmark]
    [Arguments(1)]
    [Arguments(7)]
    [Arguments(15)]
    [Arguments(23)]
    [Arguments(31)]
    public int Masked16(int tail)
    {
        ref byte src = ref MemoryMarshal.GetReference(_keys2.AsSpan());
        Vector512<ushort> lanes = Vector512.LoadUnsafe(ref src).AsUInt16();
        Vector512<ushort> gt = Vector512.GreaterThan(lanes, Vector512.Create(_search16));
        ulong kmask = (1UL << tail) - 1;
        ulong gtMask = gt.ExtractMostSignificantBits() & kmask;
        if (gtMask != 0) return BitOperations.TrailingZeroCount(gtMask) - 1;
        return tail - 1;
    }

    [Benchmark]
    [Arguments(1)]
    [Arguments(7)]
    [Arguments(15)]
    [Arguments(23)]
    [Arguments(31)]
    public unsafe int TrueMasked16(int tail)
    {
        Vector512<ushort> mask = Vector512.LessThan(LaneIdx16, Vector512.Create((ushort)tail));
        Vector512<ushort> lanes = Avx512BW.MaskLoad(
            (ushort*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_keys2)),
            mask,
            Vector512<ushort>.Zero);
        Vector512<ushort> gt = Vector512.GreaterThan(lanes, Vector512.Create(_search16));
        ulong gtMask = gt.ExtractMostSignificantBits();
        if (gtMask != 0) return BitOperations.TrailingZeroCount(gtMask) - 1;
        return tail - 1;
    }

    // =====================================================================================
    //  32-bit lanes (16 per Vector512). Tail range: 1..15.
    // =====================================================================================

    [Benchmark]
    [Arguments(1)]
    [Arguments(5)]
    [Arguments(9)]
    [Arguments(13)]
    [Arguments(15)]
    public int Scalar32(int tail)
    {
        uint search = _search32;
        ref byte src = ref MemoryMarshal.GetReference(_keys4.AsSpan());
        for (int i = 0; i < tail; i++)
        {
            uint k = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, (nint)(i * 4)));
            if (k > search) return i - 1;
        }
        return tail - 1;
    }

    [Benchmark]
    [Arguments(1)]
    [Arguments(5)]
    [Arguments(9)]
    [Arguments(13)]
    [Arguments(15)]
    public int Masked32(int tail)
    {
        ref byte src = ref MemoryMarshal.GetReference(_keys4.AsSpan());
        Vector512<uint> lanes = Vector512.LoadUnsafe(ref src).AsUInt32();
        Vector512<uint> gt = Vector512.GreaterThan(lanes, Vector512.Create(_search32));
        ulong kmask = (1UL << tail) - 1;
        ulong gtMask = gt.ExtractMostSignificantBits() & kmask;
        if (gtMask != 0) return BitOperations.TrailingZeroCount(gtMask) - 1;
        return tail - 1;
    }

    [Benchmark]
    [Arguments(1)]
    [Arguments(5)]
    [Arguments(9)]
    [Arguments(13)]
    [Arguments(15)]
    public unsafe int TrueMasked32(int tail)
    {
        Vector512<uint> mask = Vector512.LessThan(LaneIdx32, Vector512.Create((uint)tail));
        Vector512<uint> lanes = Avx512F.MaskLoad(
            (uint*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_keys4)),
            mask,
            Vector512<uint>.Zero);
        Vector512<uint> gt = Vector512.GreaterThan(lanes, Vector512.Create(_search32));
        ulong gtMask = gt.ExtractMostSignificantBits();
        if (gtMask != 0) return BitOperations.TrailingZeroCount(gtMask) - 1;
        return tail - 1;
    }

    // =====================================================================================
    //  64-bit lanes (8 per Vector512). Tail range: 1..7.
    // =====================================================================================

    [Benchmark]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(4)]
    [Arguments(6)]
    [Arguments(7)]
    public int Scalar64(int tail)
    {
        ulong search = _search64;
        ref byte src = ref MemoryMarshal.GetReference(_keys8.AsSpan());
        for (int i = 0; i < tail; i++)
        {
            ulong k = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, (nint)(i * 8)));
            if (k > search) return i - 1;
        }
        return tail - 1;
    }

    [Benchmark]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(4)]
    [Arguments(6)]
    [Arguments(7)]
    public int Masked64(int tail)
    {
        ref byte src = ref MemoryMarshal.GetReference(_keys8.AsSpan());
        Vector512<ulong> lanes = Vector512.LoadUnsafe(ref src).AsUInt64();
        Vector512<ulong> gt = Vector512.GreaterThan(lanes, Vector512.Create(_search64));
        ulong kmask = (1UL << tail) - 1;
        ulong gtMask = gt.ExtractMostSignificantBits() & kmask;
        if (gtMask != 0) return BitOperations.TrailingZeroCount(gtMask) - 1;
        return tail - 1;
    }

    [Benchmark]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(4)]
    [Arguments(6)]
    [Arguments(7)]
    public unsafe int TrueMasked64(int tail)
    {
        Vector512<ulong> mask = Vector512.LessThan(LaneIdx64, Vector512.Create((ulong)tail));
        Vector512<ulong> lanes = Avx512F.MaskLoad(
            (ulong*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_keys8)),
            mask,
            Vector512<ulong>.Zero);
        Vector512<ulong> gt = Vector512.GreaterThan(lanes, Vector512.Create(_search64));
        ulong gtMask = gt.ExtractMostSignificantBits();
        if (gtMask != 0) return BitOperations.TrailingZeroCount(gtMask) - 1;
        return tail - 1;
    }
}
