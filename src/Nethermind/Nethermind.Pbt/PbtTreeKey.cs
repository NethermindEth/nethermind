// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Pbt;

/// <summary>An immutable account- or code-zone tree key of any length up to <see cref="PbtFullKey.KeyLength"/>.</summary>
/// <remarks>Backed by a zero-padded <see cref="PbtFullKey"/> plus its logical length. The default value is not a valid complete key.</remarks>
public readonly struct PbtTreeKey : IPbtKey<PbtTreeKey>
{
    public const int MaxLength = PbtFullKey.KeyLength;
    /// <inheritdoc/>
    public static int Capacity => MaxLength;
    /// <inheritdoc/>
    public static PbtTreeKey Create(ReadOnlySpan<byte> bytes) => new(bytes);
    private readonly PbtFullKey _key;

    public PbtTreeKey(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), $"Key length must be between 1 and {MaxLength} bytes.");
        }

        _key = PbtFullKey.ZeroPad(bytes);
        Length = bytes.Length;
    }

    private PbtTreeKey(in PbtFullKey key)
    {
        _key = key;
        Length = PbtFullKey.KeyLength;
    }

    /// <summary>Widens a key without changing its bytes.</summary>
    public static explicit operator PbtTreeKey(in PbtFullKey key) => new(key);

    /// <summary>Narrows a key, rejecting any length other than <see cref="PbtFullKey.KeyLength"/>.</summary>
    public static explicit operator PbtFullKey(in PbtTreeKey key) =>
        key.Length == PbtFullKey.KeyLength ? key._key : throw new ArgumentOutOfRangeException(nameof(key));

    public int Length { get; }
    public int BitLength => Length * 8;
    [UnscopedRef]
    public ReadOnlySpan<byte> Bytes => _key.Bytes[..Length];

    public int GetBit(int bitIndex) => PbtKeyOperations.GetBit(Bytes, bitIndex);

    public bool IsPrefixOf(in PbtTreeKey other) =>
        Length <= other.Length && other.Bytes[..Length].SequenceEqual(Bytes);

    public int FirstDifferingBit(in PbtTreeKey other, int startBit = 0) =>
        PbtKeyOperations.FirstDifferingBit(Bytes, other.Bytes, startBit);

    public int CompareTo(PbtTreeKey other) => Bytes.SequenceCompareTo(other.Bytes);
    public bool Equals(PbtTreeKey other) => Bytes.SequenceEqual(other.Bytes);
    public override bool Equals(object? obj) => obj is PbtTreeKey other && Equals(other);
    public static bool operator ==(in PbtTreeKey left, in PbtTreeKey right) => left.Equals(right);
    public static bool operator !=(in PbtTreeKey left, in PbtTreeKey right) => !left.Equals(right);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.AddBytes(Bytes);
        return hash.ToHashCode();
    }
}
