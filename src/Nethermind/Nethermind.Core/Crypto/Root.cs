// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Linq;
using System.Runtime.InteropServices;
using Nethermind.Int256;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Crypto;

public class Root : IEquatable<Root>, IComparable<Root>
{
    public const int Length = 32;

    public byte[] Bytes { get; }

    public Root()
    {
        Bytes = new byte[Length];
    }

    public Root(UInt256 span)
        : this(MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateReadOnlySpan(ref span, 1)))
    {
    }

    public Root(ReadOnlySpan<byte> span)
        : this(span.ToArray())
    {
    }

    public void AsInt(out UInt256 intRoot)
    {
        intRoot = new UInt256(Bytes.AsSpan());
    }

    public static Root Wrap(byte[] bytes)
    {
        return new Root(bytes);
    }

    private Root(byte[] bytes)
    {
        if (bytes.Length != Length)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), bytes.Length,
                $"{nameof(Root)} must have exactly {Length} bytes");
        }

        Bytes = bytes;
    }

    public Root(string hex)
    {
        byte[] bytes = Extensions.Bytes.FromHexString(hex);
        if (bytes.Length != Length)
        {
            throw new ArgumentOutOfRangeException(nameof(hex), bytes.Length, $"{nameof(Root)} must have exactly {Length} bytes");
        }

        Bytes = bytes;
    }

    public static Root Zero { get; } = new Root(new byte[Length]);

    public ReadOnlySpan<byte> AsSpan()
    {
        return new ReadOnlySpan<byte>(Bytes);
    }

    public override int GetHashCode()
    {
        return BinaryPrimitives.ReadInt32LittleEndian(AsSpan()[..4]);
    }

    public static bool operator ==(Root left, Root right)
    {
        if (ReferenceEquals(left, right)) return true;
        return left.Equals(right);
    }

    public static explicit operator Root(ReadOnlySpan<byte> span) => new Root(span);

    public static explicit operator ReadOnlySpan<byte>(Root value) => value.AsSpan();

    public static bool operator !=(Root left, Root right)
    {
        return !(left == right);
    }

    public override string ToString()
    {
        return Bytes.ToHexString(true);
    }

    public bool Equals(Root? other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;
        return Bytes.SequenceEqual(other.Bytes);
    }

    public override bool Equals(object? obj)
    {
        return ReferenceEquals(this, obj) || obj is Root other && Equals(other);
    }

    public int CompareTo(Root? other)
    {
        // lexicographic compare
        return other is null ? 1 : AsSpan().SequenceCompareTo(other.AsSpan());
    }
}
