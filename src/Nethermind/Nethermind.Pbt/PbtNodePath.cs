// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Pbt;

/// <summary>Identifies a canonical tree node by its consumed MSB-first key path.</summary>
/// <remarks>Paths are limited to 272 bits.</remarks>
public readonly struct PbtNodePath : IPbtNodePath<PbtNodePath>, IEquatable<PbtNodePath>, IComparable<PbtNodePath>
{
    private readonly PbtFullKey _path;

    /// <inheritdoc/>
    public static int MaxBitDepth => PbtFullKey.MaxLength * 8;
    /// <inheritdoc/>
    public static PbtNodePath Create(ReadOnlySpan<byte> path, int bitDepth) => new(path, bitDepth);

    public PbtNodePath(ReadOnlySpan<byte> path, int bitDepth)
    {
        PbtPathOperations.Validate(path, bitDepth, MaxBitDepth);
        _path = path.IsEmpty ? default : new PbtFullKey(path);
        BitDepth = bitDepth;
    }

    public int BitDepth { get; }
    [UnscopedRef]
    public ReadOnlySpan<byte> Path => _path.Bytes;

    public byte[] Encode() => PbtPathOperations.Encode(this);
    public static PbtNodePath Decode(ReadOnlySpan<byte> encoding) => PbtPathOperations.Decode<PbtNodePath>(encoding);

    internal static PbtNodePath FromKey(PbtFullKey key, int bitDepth) => PbtPathOperations.FromKey<PbtNodePath>(key.Bytes, bitDepth);

    internal PbtNodePath Append(PbtBitPrefix prefix, int direction) => Append(prefix.Bytes, prefix.BitCount, direction);
    internal PbtNodePath Append(ReadOnlySpan<byte> prefix, int bitCount, int direction) =>
        PbtPathOperations.Append<PbtNodePath>(this, prefix, bitCount, direction);

    public int CompareTo(PbtNodePath other) => PbtPathOperations.Compare(this, other);
    public bool Equals(PbtNodePath other) => PbtPathOperations.Equal(this, other);
    public int CompareTo(IPbtNodePath? other) => PbtPathOperations.Compare(this, other);
    public bool Equals(IPbtNodePath? other) => PbtPathOperations.Equal(this, other);
    public override bool Equals(object? obj) => obj is IPbtNodePath other && Equals(other);
    public override int GetHashCode() => PbtPathOperations.Hash(this);
}
