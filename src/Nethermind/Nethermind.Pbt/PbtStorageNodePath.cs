// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>Identifies a canonical tree node by its consumed MSB-first key path.</summary>
/// <remarks>Paths are limited to 528 bits.</remarks>
public sealed class PbtStorageNodePath : IPbtNodePath<PbtStorageNodePath>
{
    private readonly PbtStorageFullKey _path;

    /// <inheritdoc/>
    public static int MaxBitDepth => PbtStorageFullKey.MaxLength * 8;
    /// <inheritdoc/>
    public static PbtStorageNodePath Create(ReadOnlySpan<byte> path, int bitDepth) => new(path, bitDepth);

    public PbtStorageNodePath(ReadOnlySpan<byte> path, int bitDepth)
    {
        PbtPathOperations.Validate(path, bitDepth, MaxBitDepth);
        _path = path.IsEmpty ? default : new PbtStorageFullKey(path);
        BitDepth = bitDepth;
    }

    public int BitDepth { get; }
    public ReadOnlySpan<byte> Path => _path.Bytes;

    public byte[] Encode() => PbtPathOperations.Encode(this);
    public static PbtStorageNodePath Decode(ReadOnlySpan<byte> encoding) => PbtPathOperations.Decode<PbtStorageNodePath>(encoding);

    internal static PbtStorageNodePath FromKey(PbtStorageFullKey key, int bitDepth) => PbtPathOperations.FromKey<PbtStorageNodePath>(key.Bytes, bitDepth);

    internal PbtStorageNodePath Append(PbtBitPrefix prefix, int direction) => Append(prefix.Bytes, prefix.BitCount, direction);
    internal PbtStorageNodePath Append(ReadOnlySpan<byte> prefix, int bitCount, int direction) =>
        PbtPathOperations.Append<PbtStorageNodePath>(this, prefix, bitCount, direction);

    public int CompareTo(IPbtNodePath? other) => PbtPathOperations.Compare(this, other);
    public bool Equals(IPbtNodePath? other) => PbtPathOperations.Equal(this, other);
    public override bool Equals(object? obj) => obj is IPbtNodePath other && Equals(other);
    public override int GetHashCode() => PbtPathOperations.Hash(this);
}
