// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nethermind.Pbt;

/// <summary>An immutable complete EIP-8297 tree key of any zone and length.</summary>
/// <remarks>Keys larger than 66 bytes are unsupported. The default value is not a valid complete key. Storage-zone keys of the fixed 66-byte layout are also represented by <see cref="PbtStorageFullKey"/>.</remarks>
public readonly struct PbtTreeKey : IPbtKey<PbtTreeKey>
{
    public const int MaxLength = 66;
    /// <inheritdoc/>
    public static int Capacity => MaxLength;
    /// <inheritdoc/>
    public static PbtTreeKey Create(ReadOnlySpan<byte> bytes) => new(bytes);
    private readonly KeyBytes _bytes;

    [InlineArray(MaxLength)]
    private struct KeyBytes
    {
        private byte _element0;
    }

    public PbtTreeKey(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), $"Key length must be between 1 and {MaxLength} bytes.");
        }

        bytes.CopyTo(_bytes);
        Length = bytes.Length;
    }

    /// <summary>Widens a key without changing its bytes.</summary>
    public static explicit operator PbtTreeKey(in PbtFullKey key) => key.Length == 0 ? default : new(key.Bytes);

    /// <summary>Narrows a key, rejecting keys longer than 34 bytes.</summary>
    public static explicit operator PbtFullKey(in PbtTreeKey key) => key.Length == 0 ? default : new(key.Bytes);

    public int Length { get; }
    public int BitLength => Length * 8;
    [UnscopedRef]
    public ReadOnlySpan<byte> Bytes => _bytes[..Length];

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
