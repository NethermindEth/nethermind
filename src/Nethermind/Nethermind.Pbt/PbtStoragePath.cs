// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nethermind.Pbt;

/// <summary>An immutable complete EIP-8297 storage-zone key: zone byte, address hash, tree-index hash and slot byte.</summary>
/// <remarks>
/// Every key is exactly <see cref="KeyLength"/> bytes. Header slots derive account-zone keys instead; see
/// <see cref="PbtStorageTreeKey"/> for those.
/// </remarks>
public readonly struct PbtStoragePath : IPbtKey<PbtStoragePath>
{
    public const int KeyLength = Eip8297KeyDerivation.StorageKeyLength;
    /// <inheritdoc/>
    public static int Capacity => KeyLength;
    /// <inheritdoc/>
    public static PbtStoragePath Create(ReadOnlySpan<byte> bytes) => new(bytes);
    private readonly KeyBytes _bytes;

    [InlineArray(KeyLength)]
    private struct KeyBytes
    {
        private byte _element0;
    }

    public PbtStoragePath(ReadOnlySpan<byte> bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(bytes.Length, KeyLength, nameof(bytes));
        bytes.CopyTo(_bytes);
    }

    /// <summary>Narrows a key, rejecting any length other than <see cref="KeyLength"/>.</summary>
    public static explicit operator PbtStoragePath(in PbtStorageTreeKey key) => new(key.Bytes);

    /// <summary>Widens a key without changing its bytes.</summary>
    public static explicit operator PbtStorageTreeKey(in PbtStoragePath key) => new(key.Bytes);

    public int Length => KeyLength;
    public int BitLength => KeyLength * 8;
    [UnscopedRef]
    public ReadOnlySpan<byte> Bytes => _bytes;

    public int GetBit(int bitIndex) => PbtKeyOperations.GetBit(Bytes, bitIndex);

    public bool IsPrefixOf(in PbtStoragePath other) => Equals(other);

    public int FirstDifferingBit(in PbtStoragePath other, int startBit = 0) =>
        PbtKeyOperations.FirstDifferingBit(Bytes, other.Bytes, startBit);

    public int CompareTo(PbtStoragePath other) => Bytes.SequenceCompareTo(other.Bytes);
    public bool Equals(PbtStoragePath other) => Bytes.SequenceEqual(other.Bytes);
    public override bool Equals(object? obj) => obj is PbtStoragePath other && Equals(other);
    public static bool operator ==(in PbtStoragePath left, in PbtStoragePath right) => left.Equals(right);
    public static bool operator !=(in PbtStoragePath left, in PbtStoragePath right) => !left.Equals(right);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.AddBytes(Bytes);
        return hash.ToHashCode();
    }
}
