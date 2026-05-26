// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.Trie.Sparse;

/// <summary>
/// A 16-bit bitmask representing which of the 16 nibble slots (0x0–0xF) in a branch node are occupied.
/// </summary>
public readonly struct TrieMask(ushort mask) : IEquatable<TrieMask>
{
    public ushort Raw { get; } = mask;

    public static TrieMask Empty => new(0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsBitSet(int nibble) => (Raw & (1 << nibble)) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TrieMask SetBit(int nibble) => new((ushort)(Raw | (1 << nibble)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TrieMask ClearBit(int nibble) => new((ushort)(Raw & ~(1 << nibble)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CountBits() => BitOperations.PopCount(Raw);

    /// <summary>
    /// Returns the dense index for a nibble within the set bits.
    /// E.g., if mask = {2, 5, 9} and nibble = 5, returns 1 (the second set bit).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DenseIndex(int nibble) => BitOperations.PopCount((uint)(Raw & ((1 << nibble) - 1)));

    public bool Equals(TrieMask other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is TrieMask other && Equals(other);
    public override int GetHashCode() => Raw;
    public static bool operator ==(TrieMask left, TrieMask right) => left.Raw == right.Raw;
    public static bool operator !=(TrieMask left, TrieMask right) => left.Raw != right.Raw;
    public override string ToString() => $"TrieMask(0x{Raw:X4}, count={CountBits()})";
}
