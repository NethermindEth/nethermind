// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Collections;

/// <summary>Sizing, bucket selection and unchecked indexing shared by the zkVM <see cref="OptimizedDictionary{TKey,TValue}"/> and <see cref="OptimizedHashSet{T}"/>.</summary>
internal static class OptimizedHashing
{
    private const int MinSize = 4;
    private const ulong FibonacciMultiplier = 0x9E3779B97F4A7C15;

    /// <summary>The bucket of a hash code in a table of <c>2^(64 - shift)</c> buckets.</summary>
    /// <remarks>Multiplicative hashing takes the top bits, so a hash code with weak low bits still spreads over the buckets.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long BucketIndex(ulong hashCode, long shift) => (long)((hashCode * FibonacciMultiplier) >> (int)shift);

    /// <summary>The power-of-two table size holding <paramref name="capacity"/> entries.</summary>
    public static int SizeFor(int capacity) =>
        capacity <= MinSize ? MinSize : (int)BitOperations.RoundUpToPowerOf2((uint)capacity);

    public static long ShiftFor(int size) => 64 - BitOperations.Log2((uint)size);

    public static int GrowSize(int size) => size == 0 ? MinSize : checked(size * 2);

    /// <summary>An element of <paramref name="array"/> without a bounds check.</summary>
    /// <remarks>Callers index only below their table size or entry count, so the check would only add a 32-bit length load.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref T At<T>(T[] array, long index) => ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array), (nint)index);

    /// <summary>The comparer to keep: <see langword="null"/> when a value-type key's default equality applies.</summary>
    /// <remarks>Mirrors <see cref="Dictionary{TKey,TValue}"/>, whose value-type keys without a comparer call their own equality directly.</remarks>
    public static IEqualityComparer<T>? ResolveComparer<T>(IEqualityComparer<T>? comparer)
    {
        if (typeof(T).IsValueType)
        {
            return comparer is null || ReferenceEquals(comparer, EqualityComparer<T>.Default) ? null : comparer;
        }

        return comparer ?? EqualityComparer<T>.Default;
    }
}
