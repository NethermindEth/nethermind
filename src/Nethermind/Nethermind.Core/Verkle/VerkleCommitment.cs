// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Verkle;

public class VerkleCommitment: IEquatable<VerkleCommitment>, IComparable<VerkleCommitment>
{
    public const int Size = 32;

    public const int MemorySize =
        MemorySizes.SmallObjectOverhead +
        MemorySizes.RefSize +
        MemorySizes.ArrayOverhead +
        Size -
        MemorySizes.SmallObjectFreeDataSize;

    /// <returns>
    ///     <string>0x0000000000000000000000000000000000000000000000000000000000000000</string>
    /// </returns>
    public static VerkleCommitment Zero { get; } = new(new byte[Size]);
    public static VerkleCommitment EmptyTreeHash = Zero;

    /// <summary>
    ///     <string>0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff</string>
    /// </summary>
    public static VerkleCommitment MaxValue { get; } = new("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

    public byte[] Bytes { get; }

    public VerkleCommitment(string hexString)
        : this(Extensions.Bytes.FromHexString(hexString)) { }

    public VerkleCommitment(byte[] bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException($"{nameof(VerkleCommitment)} must be {Size} bytes and was {bytes.Length} bytes", nameof(bytes));
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

    public bool Equals(VerkleCommitment? other)
    {
        if (other is null)
        {
            return false;
        }

        return Extensions.Bytes.AreEqual(other.Bytes, Bytes);
    }

    public int CompareTo(VerkleCommitment? other)
    {
        return Extensions.Bytes.Comparer.Compare(Bytes, other?.Bytes);
    }

    public override bool Equals(object? obj)
    {
        return obj?.GetType() == typeof(VerkleCommitment) && Equals((VerkleCommitment)obj);
    }

    public override int GetHashCode()
    {
        long v0 = Unsafe.ReadUnaligned<long>(ref MemoryMarshal.GetArrayDataReference(Bytes));
        long v1 = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Bytes), sizeof(long)));
        long v2 = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Bytes), sizeof(long) * 2));
        long v3 = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Bytes), sizeof(long) * 3));
        v0 ^= v1;
        v2 ^= v3;
        v0 ^= v2;

        return (int)v0 ^ (int)(v0 >> 32);
    }

    public static bool operator ==(VerkleCommitment? a, VerkleCommitment? b)
    {
        if (a is null)
        {
            return b is null;
        }

        if (b is null)
        {
            return false;
        }

        return Extensions.Bytes.AreEqual(a.Bytes, b.Bytes);
    }

    public static bool operator !=(VerkleCommitment? a, VerkleCommitment? b)
    {
        return !(a == b);
    }

    public static bool operator >(VerkleCommitment? k1, VerkleCommitment? k2)
    {
        return Extensions.Bytes.Comparer.Compare(k1?.Bytes, k2?.Bytes) > 0;
    }

    public static bool operator <(VerkleCommitment? k1, VerkleCommitment? k2)
    {
        return Extensions.Bytes.Comparer.Compare(k1?.Bytes, k2?.Bytes) < 0;
    }

    public static bool operator >=(VerkleCommitment? k1, VerkleCommitment? k2)
    {
        return Extensions.Bytes.Comparer.Compare(k1?.Bytes, k2?.Bytes) >= 0;
    }

    public static bool operator <=(VerkleCommitment? k1, VerkleCommitment? k2)
    {
        return Extensions.Bytes.Comparer.Compare(k1?.Bytes, k2?.Bytes) <= 0;
    }
}
