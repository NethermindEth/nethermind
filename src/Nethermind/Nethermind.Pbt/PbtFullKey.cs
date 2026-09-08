// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nethermind.Pbt;

/// <summary>An immutable complete EIP-8297 tree key.</summary>
/// <remarks>Keys larger than 34 bytes are unsupported. The default value is not a valid complete key.</remarks>
public readonly struct PbtFullKey : IPbtKey<PbtFullKey>
{
    public const int MaxLength = 34;
    /// <inheritdoc/>
    public static int Capacity => MaxLength;
    /// <inheritdoc/>
    public static PbtFullKey Create(ReadOnlySpan<byte> bytes) => new(bytes);
    private readonly KeyBytes _bytes;

    [InlineArray(MaxLength)]
    private struct KeyBytes
    {
        private byte _element0;
    }

    public PbtFullKey(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), $"Key length must be between 1 and {MaxLength} bytes.");
        }

        bytes.CopyTo(_bytes);
        Length = bytes.Length;
    }

    public int Length { get; }
    public int BitLength => Length * 8;
    [UnscopedRef]
    public ReadOnlySpan<byte> Bytes => _bytes[..Length];

    public int GetBit(int bitIndex) => PbtKeyOperations.GetBit(Bytes, bitIndex);

    public bool IsPrefixOf(PbtFullKey other) =>
        Length <= other.Length && other.Bytes[..Length].SequenceEqual(Bytes);

    public int FirstDifferingBit(PbtFullKey other, int startBit = 0) =>
        PbtKeyOperations.FirstDifferingBit(Bytes, other.Bytes, startBit);

    public int CompareTo(PbtFullKey other) => Bytes.SequenceCompareTo(other.Bytes);
    public bool Equals(PbtFullKey other) => Bytes.SequenceEqual(other.Bytes);
    public override bool Equals(object? obj) => obj is PbtFullKey other && Equals(other);
    public static bool operator ==(PbtFullKey left, PbtFullKey right) => left.Equals(right);
    public static bool operator !=(PbtFullKey left, PbtFullKey right) => !left.Equals(right);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.AddBytes(Bytes);
        return hash.ToHashCode();
    }
}
