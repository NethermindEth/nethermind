// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core.Verkle;

[DebuggerStepThrough]
[DebuggerDisplay("{ToString()}")]
public readonly struct ValuePedersen : IEquatable<ValuePedersen>, IComparable<ValuePedersen>, IEquatable<Pedersen>
{
    private readonly Vector256<byte> Bytes;

    public const int MemorySize = 32;

    public Span<byte> BytesAsSpan => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref Unsafe.AsRef(in Bytes), 1));

    public ReadOnlySpan<byte> Span => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in Bytes), 1));

    /// <returns>
    ///     <string>0x0000000000000000000000000000000000000000000000000000000000000000</string>
    /// </returns>
    public static ValuePedersen Zero { get; } = default;

    /// <summary>
    ///     <string>0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff</string>
    /// </summary>
    public static ValuePedersen MaxValue { get; } = new("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

    public static implicit operator ValuePedersen(Pedersen? keccak)
    {
        return new ValuePedersen(keccak?.Bytes);
    }

    public ValuePedersen(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            Bytes = default;
            return;
        }

        Debug.Assert(bytes.Length == MemorySize);
        Bytes = Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetArrayDataReference(bytes));
    }

    public ValuePedersen(string? hex)
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

    public ValuePedersen(Span<byte> bytes)
        : this((ReadOnlySpan<byte>)bytes) { }

    public ValuePedersen(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            Bytes = default;
            return;
        }

        Debug.Assert(bytes.Length == ValuePedersen.MemorySize);
        Bytes = Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetReference(bytes));
    }

    public override bool Equals(object? obj) => obj is ValuePedersen keccak && Equals(keccak);

    public bool Equals(ValuePedersen other) => Bytes.Equals(other.Bytes);

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

    public int CompareTo(ValuePedersen other)
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

    public static bool operator ==(ValuePedersen left, ValuePedersen right) => left.Equals(right);

    public static bool operator !=(ValuePedersen left, ValuePedersen right) => !(left == right);
    public static bool operator >(ValuePedersen left, ValuePedersen right) => left.CompareTo(right) > 0;
    public static bool operator <(ValuePedersen left, ValuePedersen right) => left.CompareTo(right) < 0;
    public static bool operator >=(ValuePedersen left, ValuePedersen right) => left.CompareTo(right) >= 0;
    public static bool operator <=(ValuePedersen left, ValuePedersen right) => left.CompareTo(right) <= 0;

    public Pedersen ToPedersen()
    {
        return new Pedersen(BytesAsSpan.ToArray());
    }
}

/// <summary>
/// Used as dictionary key with implicit conversion to de-virtualize comparisons
/// </summary>
[DebuggerStepThrough]
public readonly struct PedersenKey : IEquatable<PedersenKey>, IComparable<PedersenKey>
{
    public byte[] Bytes { get; }

    private PedersenKey(byte[] bytes)
    {
        Bytes = bytes;
    }

    public static implicit operator PedersenKey(Pedersen k) => new(k.Bytes);

    public int CompareTo(PedersenKey other)
    {
        return Nethermind.Core.Extensions.Bytes.Comparer.Compare(Bytes, other.Bytes);
    }

    public bool Equals(PedersenKey other)
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
        return obj is PedersenKey key && Equals(key);
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

[DebuggerStepThrough]
public class Pedersen : IEquatable<Pedersen>, IComparable<Pedersen>
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
    public static Pedersen Zero { get; } = new(new byte[Size]);

    /// <summary>
    ///     <string>0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff</string>
    /// </summary>
    public static Pedersen MaxValue { get; } = new("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

    public byte[] Bytes { get; }
    public ReadOnlySpan<byte> BytesAsSpan => Bytes;
    public ReadOnlySpan<byte> StemAsSpan => new(Bytes, 0, 31);

    public ReadOnlySpan<byte> NodeKeyAsSpan(int i) => new(Bytes, 0, i);

    public byte SuffixByte
    {
        get => Bytes[31];
        set => Bytes[31] = value;
    }

    public Pedersen(string hexString)
        : this(Nethermind.Core.Extensions.Bytes.FromHexString(hexString)) { }

    public Pedersen(byte[] bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException($"{nameof(Pedersen)} must be {Size} bytes and was {bytes.Length} bytes", nameof(bytes));
        }

        Bytes = bytes.AsSpan().ToArray();
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

    public static implicit operator Pedersen(byte[] bytes)
    {
        return new Pedersen(bytes);
    }

    [DebuggerStepThrough]
    public static Pedersen Compute(ReadOnlySpan<byte> address20, UInt256 treeIndex)
    {
        return new Pedersen(PedersenHash.ComputeHashBytes(address20, treeIndex));
    }

    [DebuggerStepThrough]
    public static Pedersen Compute(byte[] address20, UInt256 treeIndex)
    {
        return new Pedersen(PedersenHash.ComputeHashBytes(address20, treeIndex));
    }

    public bool Equals(Pedersen? other)
    {
        if (other is null)
        {
            return false;
        }

        return Nethermind.Core.Extensions.Bytes.AreEqual(other.Bytes, Bytes);
    }

    public int CompareTo(Pedersen? other)
    {
        return Nethermind.Core.Extensions.Bytes.Comparer.Compare(Bytes, other?.Bytes);
    }

    public override bool Equals(object? obj)
    {
        return obj?.GetType() == typeof(Pedersen) && Equals((Pedersen)obj);
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

    public static bool operator ==(Pedersen? a, Pedersen? b)
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

    public static bool operator !=(Pedersen? a, Pedersen? b)
    {
        return !(a == b);
    }

    public static bool operator >(Pedersen? k1, Pedersen? k2)
    {
        return Nethermind.Core.Extensions.Bytes.Comparer.Compare(k1?.Bytes, k2?.Bytes) > 0;
    }

    public static bool operator <(Pedersen? k1, Pedersen? k2)
    {
        return Nethermind.Core.Extensions.Bytes.Comparer.Compare(k1?.Bytes, k2?.Bytes) < 0;
    }

    public static bool operator >=(Pedersen? k1, Pedersen? k2)
    {
        return Nethermind.Core.Extensions.Bytes.Comparer.Compare(k1?.Bytes, k2?.Bytes) >= 0;
    }

    public static bool operator <=(Pedersen? k1, Pedersen? k2)
    {
        return Nethermind.Core.Extensions.Bytes.Comparer.Compare(k1?.Bytes, k2?.Bytes) <= 0;
    }

    public PedersenStructRef ToStructRef() => new(Bytes);
}

public ref struct PedersenStructRef
{
    public const int Size = 32;

    public int MemorySize => MemorySizes.ArrayOverhead + Size;

    public Span<byte> Bytes { get; }

    public PedersenStructRef(Span<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException($"{nameof(Pedersen)} must be {Size} bytes and was {bytes.Length} bytes", nameof(bytes));
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

    [DebuggerStepThrough]
    public static PedersenStructRef Compute(byte[] address20, UInt256 treeIndex)
    {
        var result = new PedersenStructRef();
        PedersenHash.ComputeHashBytesToSpan(address20, treeIndex, result.Bytes);
        return result;
    }

    [DebuggerStepThrough]
    public static PedersenStructRef Compute(ReadOnlySpan<byte> address20, UInt256 treeIndex)
    {
        var result = new PedersenStructRef();
        PedersenHash.ComputeHashBytesToSpan(address20, treeIndex, result.Bytes);
        return result;
    }

    private static PedersenStructRef InternalCompute(ReadOnlySpan<byte> address20, UInt256 treeIndex)
    {
        var result = new PedersenStructRef();
        PedersenHash.ComputeHashBytesToSpan(address20, treeIndex, result.Bytes);
        return result;
    }

    public bool Equals(Pedersen? other)
    {
        if (other is null)
        {
            return false;
        }

        return Nethermind.Core.Extensions.Bytes.AreEqual(other.Bytes, Bytes);
    }

    public bool Equals(PedersenStructRef other) => Nethermind.Core.Extensions.Bytes.AreEqual(other.Bytes, Bytes);

    public override bool Equals(object? obj)
    {
        return obj?.GetType() == typeof(Pedersen) && Equals((Pedersen)obj);
    }

    public override int GetHashCode()
    {
        return MemoryMarshal.Read<int>(Bytes);
    }

    public static bool operator ==(PedersenStructRef a, Pedersen? b)
    {
        if (b is null)
        {
            return false;
        }

        return Nethermind.Core.Extensions.Bytes.AreEqual(a.Bytes, b.Bytes);
    }

    public static bool operator ==(Pedersen? a, PedersenStructRef b)
    {
        if (a is null)
        {
            return false;
        }

        return Nethermind.Core.Extensions.Bytes.AreEqual(a.Bytes, b.Bytes);
    }

    public static bool operator ==(PedersenStructRef a, PedersenStructRef b)
    {
        return Nethermind.Core.Extensions.Bytes.AreEqual(a.Bytes, b.Bytes);
    }

    public static bool operator !=(PedersenStructRef a, Pedersen b)
    {
        return !(a == b);
    }

    public static bool operator !=(Pedersen a, PedersenStructRef b)
    {
        return !(a == b);
    }

    public static bool operator !=(PedersenStructRef a, PedersenStructRef b)
    {
        return !(a == b);
    }

    public Pedersen ToKeccak() => new(Bytes.ToArray());
}
