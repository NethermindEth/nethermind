// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nethermind.Pbt;

/// <summary>An immutable complete EIP-8297 storage-zone key: zone byte, address hash, tree-index hash and slot byte.</summary>
/// <remarks>
/// Every key is exactly <see cref="KeyLength"/> bytes, so <see cref="TrieUpdater"/> skips its variable-length
/// terminal handling. Header slots derive account-zone keys instead; see <see cref="PbtStorageTreeKey"/> for those.
/// </remarks>
public readonly struct PbtStorageFullKey : IPbtKey<PbtStorageFullKey>
{
    public const int KeyLength = Eip8297KeyDerivation.StorageKeyLength;
    /// <inheritdoc/>
    public static bool IsFixedLength => true;
    /// <inheritdoc/>
    public static int Capacity => KeyLength;
    /// <inheritdoc/>
    public static PbtStorageFullKey Create(ReadOnlySpan<byte> bytes) => new(bytes);
    private readonly KeyBytes _bytes;

    [InlineArray(KeyLength)]
    private struct KeyBytes
    {
        private byte _element0;
    }

    public PbtStorageFullKey(ReadOnlySpan<byte> bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(bytes.Length, KeyLength, nameof(bytes));
        bytes.CopyTo(_bytes);
    }

    /// <summary>Narrows a key, rejecting any length other than <see cref="KeyLength"/>.</summary>
    public static explicit operator PbtStorageFullKey(in PbtStorageTreeKey key) => new(key.Bytes);

    /// <summary>Widens a key without changing its bytes.</summary>
    public static explicit operator PbtStorageTreeKey(in PbtStorageFullKey key) => new(key.Bytes);

    public int Length => KeyLength;
    public int BitLength => KeyLength * 8;
    [UnscopedRef]
    public ReadOnlySpan<byte> Bytes => _bytes;

    public int GetBit(int bitIndex) => PbtKeyOperations.GetBit(Bytes, bitIndex);

    public bool IsPrefixOf(in PbtStorageFullKey other) => Equals(other);

    public int FirstDifferingBit(in PbtStorageFullKey other, int startBit = 0) =>
        PbtKeyOperations.FirstDifferingBit(Bytes, other.Bytes, startBit);

    public int CompareTo(PbtStorageFullKey other) => Bytes.SequenceCompareTo(other.Bytes);
    public bool Equals(PbtStorageFullKey other) => Bytes.SequenceEqual(other.Bytes);
    public override bool Equals(object? obj) => obj is PbtStorageFullKey other && Equals(other);
    public static bool operator ==(in PbtStorageFullKey left, in PbtStorageFullKey right) => left.Equals(right);
    public static bool operator !=(in PbtStorageFullKey left, in PbtStorageFullKey right) => !left.Equals(right);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.AddBytes(Bytes);
        return hash.ToHashCode();
    }
}
