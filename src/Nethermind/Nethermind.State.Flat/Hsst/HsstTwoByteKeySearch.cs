// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// SIMD-vectorised <c>lower_bound</c> over an LE-stored 2-byte-key array, shared by
/// <see cref="HsstTwoByteSlotValueReader"/> and <see cref="HsstTwoByteSlotValueLargeReader"/>.
///
/// Keys are stored byte-reversed (LE) so that a native <c>u16</c> load over a stored key
/// recovers the BE numeric value of the original input — matching
/// <see cref="HsstPackedArrayBuilder{TWriter}"/>'s LE-stored convention for 2-byte keys.
/// That makes lexicographic byte compare equivalent to unsigned numeric compare on the
/// loaded <c>ushort</c>, so a single SIMD <c>GreaterThanOrEqual</c> evaluates 16 or 32
/// keys per iteration.
/// </summary>
internal static class HsstTwoByteKeySearch
{
    /// <summary>
    /// Smallest <c>i</c> in <c>[0, count]</c> where the i-th LE-stored key, interpreted as
    /// a BE-numeric <c>ushort</c>, is <c>&gt;= </c> <paramref name="targetBe"/>'s
    /// BE-numeric value. Returns <paramref name="count"/> when every stored key is less
    /// than the target.
    /// </summary>
    /// <param name="keys">LE-stored 2-byte keys, packed (<c>2 * count</c> bytes).</param>
    /// <param name="count">Number of stored keys.</param>
    /// <param name="targetBe">Target key in input (BE / lex) byte order; exactly 2 bytes.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LowerBoundLeStored(ReadOnlySpan<byte> keys, int count, scoped ReadOnlySpan<byte> targetBe)
    {
        if (count == 0) return 0;

        // Target in BE numeric form. The on-disk LE-stored bytes for a key K (where K's
        // input bytes were [B0, B1] in BE) are stored as [B1, B0], so reading two
        // consecutive stored bytes via `BinaryPrimitives.ReadUInt16LittleEndian` recovers
        // (B0 << 8) | B1 — exactly the BE numeric value of K. Comparing that against the
        // BE-numeric target gives lex order.
        ushort search = (ushort)((targetBe[0] << 8) | targetBe[1]);
        ref byte src = ref MemoryMarshal.GetReference(keys);
        int i = 0;

        if (Vector512.IsHardwareAccelerated)
        {
            Vector512<ushort> searchVec = Vector512.Create(search);
            while (i + 32 <= count)
            {
                Vector512<ushort> lanes = Vector512.LoadUnsafe(ref src, (nuint)(i * 2)).AsUInt16();
                Vector512<ushort> ge = Vector512.GreaterThanOrEqual(lanes, searchVec);
                ulong mask = ge.ExtractMostSignificantBits();
                if (mask != 0)
                    return i + BitOperations.TrailingZeroCount(mask);
                i += 32;
            }
        }
        else if (Vector256.IsHardwareAccelerated)
        {
            Vector256<ushort> searchVec = Vector256.Create(search);
            while (i + 16 <= count)
            {
                Vector256<ushort> lanes = Vector256.LoadUnsafe(ref src, (nuint)(i * 2)).AsUInt16();
                Vector256<ushort> ge = Vector256.GreaterThanOrEqual(lanes, searchVec);
                uint mask = ge.ExtractMostSignificantBits();
                if (mask != 0)
                    return i + BitOperations.TrailingZeroCount(mask);
                i += 16;
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            Vector128<ushort> searchVec = Vector128.Create(search);
            while (i + 8 <= count)
            {
                Vector128<ushort> lanes = Vector128.LoadUnsafe(ref src, (nuint)(i * 2)).AsUInt16();
                Vector128<ushort> ge = Vector128.GreaterThanOrEqual(lanes, searchVec);
                uint mask = ge.ExtractMostSignificantBits();
                if (mask != 0)
                    return i + BitOperations.TrailingZeroCount(mask);
                i += 8;
            }
        }

        // Scalar tail / unaccelerated fallback. `ReadUInt16LittleEndian` on the LE-stored
        // bytes recovers the BE numeric value, same comparison basis as `search`.
        for (; i < count; i++)
        {
            ushort lane = BinaryPrimitives.ReadUInt16LittleEndian(keys.Slice(i * 2, 2));
            if (lane >= search) return i;
        }
        return count;
    }

    /// <summary>
    /// Read the i-th LE-stored key from <paramref name="keys"/> as its BE-numeric value.
    /// Use to compare against an already-derived BE-numeric target (e.g. from
    /// <see cref="LowerBoundLeStored"/>'s scalar tail).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadKeyAt(ReadOnlySpan<byte> keys, int idx)
        => BinaryPrimitives.ReadUInt16LittleEndian(keys.Slice(idx * 2, 2));
}
