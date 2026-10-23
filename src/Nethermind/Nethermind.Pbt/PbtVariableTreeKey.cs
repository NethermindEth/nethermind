// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core.Extensions;

namespace Nethermind.Pbt;

/// <summary>An immutable complete EIP-8297 tree key of any zone and length.</summary>
/// <remarks>Keys larger than 66 bytes are unsupported. The default value is not a valid complete key. Storage-zone keys of the fixed 66-byte layout are also represented by <see cref="PbtStoragePath"/>.</remarks>
public readonly struct PbtVariableTreeKey : IPbtKey<PbtVariableTreeKey>
{
    public const int MaxLength = Eip8297KeyDerivation.StorageKeyLength;
    /// <inheritdoc/>
    public static int Capacity => MaxLength;
    /// <inheritdoc/>
    public static PbtVariableTreeKey Create(ReadOnlySpan<byte> bytes) => new(bytes);
    private readonly KeyBytes _bytes;

    [InlineArray(MaxLength)]
    private struct KeyBytes
    {
        private byte _element0;
    }

    public PbtVariableTreeKey(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), $"Key length must be between 1 and {MaxLength} bytes.");
        }

        bytes.CopyTo(_bytes);
        Length = bytes.Length;
    }

    /// <summary>Widens a key without changing its bytes.</summary>
    public static explicit operator PbtVariableTreeKey(in PbtPath key) => new(key.Bytes);

    /// <summary>Widens a key without changing its bytes.</summary>
    public static explicit operator PbtVariableTreeKey(in PbtStoragePath key) => new(key.Bytes);

    /// <summary>Narrows a key, rejecting any length other than <see cref="PbtPath.KeyLength"/>.</summary>
    public static explicit operator PbtPath(in PbtVariableTreeKey key) => new(key.Bytes);

    public int Length { get; }
    public int BitLength => Length * 8;
    [UnscopedRef]
    public ReadOnlySpan<byte> Bytes => _bytes[..Length];

    public int GetBit(int bitIndex) => TrieUpdater.GetBit(Bytes, bitIndex);

    public int FirstDifferingBit(in PbtVariableTreeKey other, int startBit) =>
        PbtKeyOperations.FirstDifferingBit(Bytes, other.Bytes, startBit);

    public int CompareTo(PbtVariableTreeKey other) => Bytes.SequenceCompareTo(other.Bytes);
    public bool Equals(PbtVariableTreeKey other) => Bytes.SequenceEqual(other.Bytes);
    public override bool Equals(object? obj) => obj is PbtVariableTreeKey other && Equals(other);
    public static bool operator ==(in PbtVariableTreeKey left, in PbtVariableTreeKey right) => left.Equals(right);
    public static bool operator !=(in PbtVariableTreeKey left, in PbtVariableTreeKey right) => !left.Equals(right);

    public override int GetHashCode() => Bytes.FastHash();
}
