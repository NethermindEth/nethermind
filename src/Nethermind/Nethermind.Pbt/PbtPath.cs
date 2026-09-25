// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nethermind.Pbt;

/// <summary>An immutable complete EIP-8297 account- or code-zone key: zone byte, 32-byte hash and sub-index byte.</summary>
/// <remarks>Every key is exactly <see cref="KeyLength"/> bytes. Shorter account-zone paths are represented by <see cref="PbtTreeKey"/>.</remarks>
public readonly struct PbtPath : IPbtKey<PbtPath>
{
    public const int KeyLength = Eip8297KeyDerivation.AccountKeyLength;
    /// <inheritdoc/>
    public static int Capacity => KeyLength;
    /// <inheritdoc/>
    public static PbtPath Create(ReadOnlySpan<byte> bytes) => new(bytes);
    private readonly KeyBytes _bytes;

    [InlineArray(KeyLength)]
    private struct KeyBytes
    {
        private byte _element0;
    }

    public PbtPath(ReadOnlySpan<byte> bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(bytes.Length, KeyLength, nameof(bytes));
        bytes.CopyTo(_bytes);
    }

    /// <summary>Creates a key from a prefix of at most <see cref="KeyLength"/> bytes, leaving the tail zero.</summary>
    internal static PbtPath ZeroPad(ReadOnlySpan<byte> prefix)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(prefix.Length, KeyLength, nameof(prefix));
        Span<byte> padded = stackalloc byte[KeyLength];
        prefix.CopyTo(padded);
        return new(padded);
    }

    public int Length => KeyLength;
    public int BitLength => KeyLength * 8;
    [UnscopedRef]
    public ReadOnlySpan<byte> Bytes => _bytes;

    public int GetBit(int bitIndex) => PbtKeyOperations.GetBit(Bytes, bitIndex);

    public bool IsPrefixOf(in PbtPath other) => Equals(other);

    public int FirstDifferingBit(in PbtPath other, int startBit = 0) =>
        PbtKeyOperations.FirstDifferingBit(Bytes, other.Bytes, startBit);

    public int CompareTo(PbtPath other) => Bytes.SequenceCompareTo(other.Bytes);
    public bool Equals(PbtPath other) => Bytes.SequenceEqual(other.Bytes);
    public override bool Equals(object? obj) => obj is PbtPath other && Equals(other);
    public static bool operator ==(PbtPath left, PbtPath right) => left.Equals(right);
    public static bool operator !=(PbtPath left, PbtPath right) => !left.Equals(right);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.AddBytes(Bytes);
        return hash.ToHashCode();
    }
}
