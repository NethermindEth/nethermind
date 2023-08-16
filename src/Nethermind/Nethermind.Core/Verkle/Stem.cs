// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Verkle;

public unsafe struct ValueStem
{
    internal const int Size = 31;
    public fixed byte Bytes[Size];

    public Span<byte> BytesAsSpan => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref this, 1));
}

/// <summary>
/// Used as dictionary key with implicit conversion to de-virtualize comparisons
/// </summary>
[DebuggerStepThrough]
public readonly struct StemKey : IEquatable<StemKey>, IComparable<StemKey>
{
    public const int Size = 31;
    public byte[] Bytes { get; }

    private StemKey(byte[] bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException($"{nameof(Stem)} must be {Size} bytes and was {bytes.Length} bytes", nameof(bytes));
        }
        Bytes = bytes;
    }

    public static implicit operator StemKey(Stem k) => new(k.Bytes);

    public int CompareTo(StemKey other)
    {
        return Nethermind.Core.Extensions.Bytes.Comparer.Compare(Bytes, other.Bytes);
    }

    public bool Equals(StemKey other)
    {
        if (ReferenceEquals(Bytes, other.Bytes))
        {
            return true;
        }

        if (Bytes is null)
        {
            return other.Bytes is null;
        }

        if (other.Bytes is null)
        {
            return false;
        }

        return Nethermind.Core.Extensions.Bytes.AreEqual(Bytes, other.Bytes);
    }

    public override bool Equals(object? obj)
    {
        return obj is StemKey key && Equals(key);
    }

    public override int GetHashCode()
    {
        if (Bytes is null) return 0;

        long v0 = Unsafe.ReadUnaligned<long>(ref MemoryMarshal.GetArrayDataReference(Bytes));
        long v1 = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Bytes), sizeof(long)));
        long v2 = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Bytes), sizeof(long) * 2));
        long v3 = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Bytes), sizeof(long) * 3 - 1));
        v3 <<= 1; // because one bit extra from previous long

        v0 ^= v1;
        v2 ^= v3;
        v0 ^= v2;

        return (int)v0 ^ (int)(v0 >> 32);
    }
}

[DebuggerStepThrough]
public class Stem : IEquatable<Stem>, IComparable<Stem>
{
    public const int Size = 31;

    /// <returns>
    ///     <string>0x0000000000000000000000000000000000000000000000000000000000000000</string>
    /// </returns>
    public static Stem Zero { get; } = new(new byte[Size]);

    /// <summary>
    ///     <string>0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff</string>
    /// </summary>
    public static Stem MaxValue { get; } = new("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

    public byte[] Bytes { get; }
    public ReadOnlySpan<byte> BytesAsSpan => Bytes;

    public Stem(string hexString)
        : this(Core.Extensions.Bytes.FromHexString(hexString)) { }

    public Stem(byte[] bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException($"{nameof(Stem)} must be {Size} bytes and was {bytes.Length} bytes", nameof(bytes));
        }

        Bytes = bytes;
    }

    public override string ToString()
    {
        return ToString(true);
    }

    public string ToShortString(bool withZeroX = true)
    {
        string hash = Bytes.ToHexString(withZeroX);
        return $"{hash[..(withZeroX ? 8 : 6)]}...{hash[^6..]}";
    }

    public string ToString(bool withZeroX)
    {
        return Bytes.ToHexString(withZeroX);
    }

    public static implicit operator Stem(byte[] bytes)
    {
        return new Stem(bytes);
    }

    public bool Equals(Stem? other)
    {
        if (other is null)
        {
            return false;
        }

        return Nethermind.Core.Extensions.Bytes.AreEqual(other.Bytes, Bytes);
    }

    public int CompareTo(Stem? other)
    {
        return Nethermind.Core.Extensions.Bytes.Comparer.Compare(Bytes, other?.Bytes);
    }

    public override bool Equals(object? obj)
    {
        return obj?.GetType() == typeof(Stem) && Equals((Stem)obj);
    }

    public override int GetHashCode()
    {
        long v0 = Unsafe.ReadUnaligned<long>(ref MemoryMarshal.GetArrayDataReference(Bytes));
        long v1 = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Bytes), sizeof(long)));
        long v2 = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Bytes), sizeof(long) * 2));
        long v3 = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Bytes), sizeof(long) * 3 - 1));
        v3 <<= 1; // because one bit extra from previous long

        v0 ^= v1;
        v2 ^= v3;
        v0 ^= v2;

        return (int)v0 ^ (int)(v0 >> 32);
    }

    public static bool operator ==(Stem? a, Stem? b)
    {
        if (a is null)
        {
            return b is null;
        }

        if (b is null)
        {
            return false;
        }

        return Nethermind.Core.Extensions.Bytes.AreEqual(a.Bytes, b.Bytes);
    }

    public static bool operator !=(Stem? a, Stem? b)
    {
        return !(a == b);
    }

    public static bool operator >(Stem? k1, Stem? k2)
    {
        return Nethermind.Core.Extensions.Bytes.Comparer.Compare(k1?.Bytes, k2?.Bytes) > 0;
    }

    public static bool operator <(Stem? k1, Stem? k2)
    {
        return Nethermind.Core.Extensions.Bytes.Comparer.Compare(k1?.Bytes, k2?.Bytes) < 0;
    }

    public static bool operator >=(Stem? k1, Stem? k2)
    {
        return Nethermind.Core.Extensions.Bytes.Comparer.Compare(k1?.Bytes, k2?.Bytes) >= 0;
    }

    public static bool operator <=(Stem? k1, Stem? k2)
    {
        return Nethermind.Core.Extensions.Bytes.Comparer.Compare(k1?.Bytes, k2?.Bytes) <= 0;
    }

}
