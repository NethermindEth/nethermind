// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Verkle;

[DebuggerStepThrough]
[DebuggerDisplay("{ToString()}")]
public readonly struct Leaf : IEquatable<Leaf>, IComparable<Leaf>
{
    private readonly Vector256<byte> Bytes;

    public const int MemorySize = 32;

    public Span<byte> BytesAsSpan => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref Unsafe.AsRef(in Bytes), 1));

    public ReadOnlySpan<byte> Span => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in Bytes), 1));

    /// <returns>
    ///     <string>0x0000000000000000000000000000000000000000000000000000000000000000</string>
    /// </returns>
    public static Leaf Zero { get; } = default;

    /// <summary>
    ///     <string>0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff</string>
    /// </summary>
    public static Leaf MaxValue { get; } = new("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

    public Leaf(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            Bytes = default;
            return;
        }

        Debug.Assert(bytes.Length == MemorySize);
        Bytes = Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetArrayDataReference(bytes));
    }

    public Leaf(string? hex)
    {
        if (hex is null || hex.Length == 0)
        {
            Bytes = default;
            return;
        }

        byte[] bytes = Nethermind.Core.Extensions.Bytes.FromHexString(hex);
        Debug.Assert(bytes.Length == MemorySize);
        Bytes = Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetArrayDataReference(bytes));
    }

    public Leaf(Span<byte> bytes)
        : this((ReadOnlySpan<byte>)bytes) { }

    public Leaf(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            Bytes = default;
            return;
        }

        Debug.Assert(bytes.Length == Leaf.MemorySize);
        Bytes = Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetReference(bytes));
    }

    public override bool Equals(object? obj) => obj is Leaf leaf && Equals(leaf);

    public bool Equals(Leaf other) => Bytes.Equals(other.Bytes);

    public bool Equals(Pedersen? other) => BytesAsSpan.SequenceEqual(other?.Bytes);

    public override int GetHashCode()
    {
        long v0 = Unsafe.As<Vector256<byte>, long>(ref Unsafe.AsRef(in Bytes));
        long v1 = Unsafe.Add(ref Unsafe.As<Vector256<byte>, long>(ref Unsafe.AsRef(in Bytes)), 1);
        long v2 = Unsafe.Add(ref Unsafe.As<Vector256<byte>, long>(ref Unsafe.AsRef(in Bytes)), 2);
        long v3 = Unsafe.Add(ref Unsafe.As<Vector256<byte>, long>(ref Unsafe.AsRef(in Bytes)), 3);
        v0 ^= v1;
        v2 ^= v3;
        v0 ^= v2;

        return (int)v0 ^ (int)(v0 >> 32);
    }

    public int CompareTo(Leaf other)
    {
        return Nethermind.Core.Extensions.Bytes.Comparer.Compare(BytesAsSpan, other.BytesAsSpan);
    }

    public override string ToString()
    {
        return ToString(true);
    }

    public string ToShortString(bool withZeroX = true)
    {
        string hash = BytesAsSpan.ToHexString(withZeroX);
        return $"{hash[..(withZeroX ? 8 : 6)]}...{hash[^6..]}";
    }

    public string ToString(bool withZeroX)
    {
        return BytesAsSpan.ToHexString(withZeroX);
    }

    public static bool operator ==(Leaf left, Leaf right) => left.Equals(right);

    public static bool operator !=(Leaf left, Leaf right) => !(left == right);
    public static bool operator >(Leaf left, Leaf right) => left.CompareTo(right) > 0;
    public static bool operator <(Leaf left, Leaf right) => left.CompareTo(right) < 0;
    public static bool operator >=(Leaf left, Leaf right) => left.CompareTo(right) >= 0;
    public static bool operator <=(Leaf left, Leaf right) => left.CompareTo(right) <= 0;

    public Pedersen ToPedersen()
    {
        return new Pedersen(BytesAsSpan.ToArray());
    }
}

public readonly struct LeafKey:  IEquatable<LeafKey>, IComparable<LeafKey>
{
    public byte[] Bytes { get; }

    private LeafKey(byte[] bytes)
    {
        Bytes = bytes;
    }

    public int CompareTo(LeafKey other)
    {
        return Core.Extensions.Bytes.Comparer.Compare(Bytes, other.Bytes);
    }

    public bool Equals(LeafKey other)
    {
        if (ReferenceEquals(Bytes, other.Bytes))
        {
            return true;
        }

        if (Bytes is null) return other.Bytes is null;

        return other.Bytes is not null && Core.Extensions.Bytes.AreEqual(Bytes, other.Bytes);
    }

    public override bool Equals(object? obj)
    {
        return obj is LeafKey key && Equals(key);
    }

    public override int GetHashCode()
    {
        if (Bytes is null) return 0;

        long v0 = Unsafe.ReadUnaligned<long>(ref MemoryMarshal.GetArrayDataReference(Bytes));
        long v1 = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Bytes), sizeof(long)));
        long v2 = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Bytes), sizeof(long) * 2));
        long v3 = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Bytes), sizeof(long) * 3));

        v0 ^= v1;
        v2 ^= v3;
        v0 ^= v2;

        return (int)v0 ^ (int)(v0 >> 32);
    }
}
