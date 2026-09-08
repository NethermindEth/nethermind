// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nethermind.Pbt;

/// <summary>An immutable complete storage-capable EIP-8297 tree key.</summary>
/// <remarks>Keys larger than 66 bytes are unsupported. The default value is not a valid complete key.</remarks>
public readonly struct PbtStorageFullKey : IPbtKey<PbtStorageFullKey>
{
    public const int MaxLength = 66;
    /// <inheritdoc/>
    public static int Capacity => MaxLength;
    /// <inheritdoc/>
    public static PbtStorageFullKey Create(ReadOnlySpan<byte> bytes) => new(bytes);
    private readonly KeyBytes _bytes;

    [InlineArray(MaxLength)]
    private struct KeyBytes
    {
        private byte _element0;
    }

    public PbtStorageFullKey(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), $"Key length must be between 1 and {MaxLength} bytes.");
        }

        bytes.CopyTo(_bytes);
        Length = bytes.Length;
    }

    /// <summary>Widens a key without changing its bytes.</summary>
    public static explicit operator PbtStorageFullKey(PbtFullKey key) => key.Length == 0 ? default : new(key.Bytes);

    /// <summary>Narrows a key, rejecting keys longer than 34 bytes.</summary>
    public static explicit operator PbtFullKey(PbtStorageFullKey key) => key.Length == 0 ? default : new(key.Bytes);

    public int Length { get; }
    public int BitLength => Length * 8;
    [UnscopedRef]
    public ReadOnlySpan<byte> Bytes => _bytes[..Length];

    public int GetBit(int bitIndex) => PbtKeyOperations.GetBit(Bytes, bitIndex);

    public bool IsPrefixOf(PbtStorageFullKey other) =>
        Length <= other.Length && other.Bytes[..Length].SequenceEqual(Bytes);

    public int FirstDifferingBit(PbtStorageFullKey other, int startBit = 0) =>
        PbtKeyOperations.FirstDifferingBit(Bytes, other.Bytes, startBit);

    public int CompareTo(PbtStorageFullKey other) => Bytes.SequenceCompareTo(other.Bytes);
    public bool Equals(PbtStorageFullKey other) => Bytes.SequenceEqual(other.Bytes);
    public override bool Equals(object? obj) => obj is PbtStorageFullKey other && Equals(other);
    public static bool operator ==(PbtStorageFullKey left, PbtStorageFullKey right) => left.Equals(right);
    public static bool operator !=(PbtStorageFullKey left, PbtStorageFullKey right) => !left.Equals(right);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.AddBytes(Bytes);
        return hash.ToHashCode();
    }
}
