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
/// Inline 96-byte BLS12-381 signature as used by beacon-chain containers.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = Length)]
public readonly struct BlsSignature : IEquatable<BlsSignature>
{
    public const int Length = 96;

    public BlsSignature(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Length)
        {
            throw new ArgumentException($"{nameof(BlsSignature)} must be {Length} bytes long", nameof(bytes));
        }

        bytes.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.As<BlsSignature, byte>(ref this), Length));
    }

    public ReadOnlySpan<byte> Bytes =>
        MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<BlsSignature, byte>(ref Unsafe.AsRef(in this)), Length);

    public bool Equals(BlsSignature other) => Bytes.SequenceEqual(other.Bytes);

    public override bool Equals(object? obj) => obj is BlsSignature other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.AddBytes(Bytes);
        return hash.ToHashCode();
    }

    public static bool operator ==(BlsSignature left, BlsSignature right) => left.Equals(right);

    public static bool operator !=(BlsSignature left, BlsSignature right) => !left.Equals(right);

    public override string ToString() => Bytes.ToHexString(true);
}

[SszVectorTypeConverter<BlsSignature>]
public static class BlsSignatureSszVectorTypeConverter
{
    public const int Length = BlsSignature.Length;

    public static BlsSignature FromSpan(ReadOnlySpan<byte> span) => new(span);

    public static void FromSpan(ReadOnlySpan<byte> span, Span<BlsSignature> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = FromSpan(span.Slice(i * Length, Length));
        }
    }

    public static void ToSpan(Span<byte> span, BlsSignature value) => value.Bytes.CopyTo(span);

    public static void ToSpan(Span<byte> span, ReadOnlySpan<BlsSignature> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            ToSpan(span.Slice(i * Length, Length), values[i]);
        }
    }

    public static void Feed(ref Merkleizer merkleizer, BlsSignature value)
    {
        Merkle.Merkleize(out UInt256 root, value.Bytes);
        merkleizer.Feed(root);
    }
}
