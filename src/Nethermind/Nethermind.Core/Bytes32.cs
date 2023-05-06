// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Linq;
using Nethermind.Core.Extensions;

namespace Nethermind.Core;

[DebuggerStepThrough]
public class Bytes32 : IEquatable<Bytes32>
{
    public const int Length = 32;

    private readonly byte[] _bytes;

    public Bytes32()
    {
        _bytes = new byte[Length];
    }

    public static Bytes32 Wrap(byte[] bytes)
    {
        return new Bytes32(bytes);
    }

    public byte[] Unwrap()
    {
        return _bytes;
    }

    private Bytes32(byte[] bytes)
    {
        if (bytes.Length != Length)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), bytes.Length,
                $"{nameof(Bytes32)} must have exactly {Length} bytes");
        }

        _bytes = bytes;
    }

    public Bytes32(ReadOnlySpan<byte> span)
    {
        if (span.Length != Length)
        {
            throw new ArgumentOutOfRangeException(nameof(span), span.Length,
                $"{nameof(Bytes32)} must have exactly {Length} bytes");
        }

        _bytes = span.ToArray();
    }

    public static Bytes32 Zero { get; } = new Bytes32(new byte[Length]);

    public ReadOnlySpan<byte> AsSpan()
    {
        return new ReadOnlySpan<byte>(_bytes);
    }

    public static bool operator ==(Bytes32 left, Bytes32 right)
    {
        return left.Equals(right);
    }

    public static explicit operator Bytes32(ReadOnlySpan<byte> span) => new Bytes32(span);

    public static explicit operator ReadOnlySpan<byte>(Bytes32 value) => value.AsSpan();

    public static bool operator !=(Bytes32 left, Bytes32 right)
    {
        return !(left == right);
    }

    public override string ToString()
    {
        return _bytes.ToHexString(true);
    }

    public Bytes32 Xor(Bytes32 other)
    {
        // if used much - optimize, for now leave this way
        return new Bytes32(other._bytes.Xor(_bytes));
    }

    public bool Equals(Bytes32? other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;
        return _bytes.SequenceEqual(other._bytes);
    }

    public override bool Equals(object? obj)
    {
        return ReferenceEquals(this, obj) || obj is Bytes32 other && Equals(other);
    }

    public override int GetHashCode()
    {
        return BinaryPrimitives.ReadInt32LittleEndian(AsSpan()[..4]);
    }
}
