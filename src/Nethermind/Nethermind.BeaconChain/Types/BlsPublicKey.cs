// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz;
using Nethermind.Serialization.Ssz.Merkleization;

namespace Nethermind.BeaconChain.Types;

/// <summary>
/// Inline 48-byte BLS12-381 public key as used by beacon-chain containers.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = Length)]
public readonly struct BlsPublicKey : IEquatable<BlsPublicKey>
{
    public const int Length = 48;

    public BlsPublicKey(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Length)
        {
            throw new ArgumentException($"{nameof(BlsPublicKey)} must be {Length} bytes long", nameof(bytes));
        }

        bytes.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.As<BlsPublicKey, byte>(ref this), Length));
    }

    public ReadOnlySpan<byte> Bytes =>
        MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<BlsPublicKey, byte>(ref Unsafe.AsRef(in this)), Length);

    public bool Equals(BlsPublicKey other) => Bytes.SequenceEqual(other.Bytes);

    public override bool Equals(object? obj) => obj is BlsPublicKey other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.AddBytes(Bytes);
        return hash.ToHashCode();
    }

    public static bool operator ==(BlsPublicKey left, BlsPublicKey right) => left.Equals(right);

    public static bool operator !=(BlsPublicKey left, BlsPublicKey right) => !left.Equals(right);

    public override string ToString() => Bytes.ToHexString(true);
}

[SszVectorTypeConverter<BlsPublicKey>]
public static class BlsPublicKeySszVectorTypeConverter
{
    public const int Length = BlsPublicKey.Length;

    public static BlsPublicKey FromSpan(ReadOnlySpan<byte> span) => new(span);

    public static void FromSpan(ReadOnlySpan<byte> span, Span<BlsPublicKey> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = FromSpan(span.Slice(i * Length, Length));
        }
    }

    public static void ToSpan(Span<byte> span, BlsPublicKey value) => value.Bytes.CopyTo(span);

    public static void ToSpan(Span<byte> span, ReadOnlySpan<BlsPublicKey> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            ToSpan(span.Slice(i * Length, Length), values[i]);
        }
    }

    public static void Feed(ref Merkleizer merkleizer, BlsPublicKey value)
    {
        Merkle.Merkleize(out UInt256 root, value.Bytes);
        merkleizer.Feed(root);
    }
}
