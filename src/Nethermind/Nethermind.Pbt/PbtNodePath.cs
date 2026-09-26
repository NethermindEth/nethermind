// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>Identifies a canonical tree node by its consumed MSB-first key path.</summary>
/// <remarks>Paths are limited to 272 bits.</remarks>
public readonly struct PbtNodePath : IPbtNodePath<PbtNodePath>, IEquatable<PbtNodePath>, IComparable<PbtNodePath>
{
    private readonly PbtTreeKey _path;

    /// <inheritdoc/>
    public static int MaxBitDepth => PbtTreeKey.MaxLength * 8;
    /// <inheritdoc/>
    public static PbtNodePath Create(ReadOnlySpan<byte> path, int bitDepth) => new(path, bitDepth, validated: true);

    public PbtNodePath(ReadOnlySpan<byte> path, int bitDepth) : this(path, bitDepth, validated: false) { }

    private PbtNodePath(ReadOnlySpan<byte> path, int bitDepth, bool validated)
    {
        if (!validated) PbtNodePathOperations.Validate(path, bitDepth, MaxBitDepth);
        _path = path.IsEmpty ? default : new PbtTreeKey(path);
        BitDepth = bitDepth;
    }

    public int BitDepth { get; }
    /// <inheritdoc/>
    public byte GetByte(int byteIndex) => _path.Bytes[byteIndex];

    /// <inheritdoc/>
    public PbtNodePath Prefix(int depth) => PbtNodePathOperations.Prefix<PbtNodePath>(_path.Bytes, BitDepth, depth);

    /// <inheritdoc/>
    public PbtNodePath AppendNib(int nibble) => AppendBits(nibble, 4);

    /// <inheritdoc/>
    public PbtNodePath AppendBits(int bits, int bitCount) =>
        PbtNodePathOperations.AppendBits<PbtNodePath>(_path.Bytes, BitDepth, bits, bitCount);
    /// <inheritdoc/>
    public TPath ToPath<TPath>() where TPath : struct, IPbtNodePath<TPath> => TPath.Create(_path.Bytes, BitDepth);

    public int CompareTo(PbtNodePath other)
    {
        int depthComparison = BitDepth.CompareTo(other.BitDepth);
        return depthComparison != 0 ? depthComparison : _path.Bytes.SequenceCompareTo(other._path.Bytes);
    }
    public bool Equals(PbtNodePath other) => BitDepth == other.BitDepth && _path.Bytes.SequenceEqual(other._path.Bytes);
    public bool Equals<TOther>(TOther other) where TOther : struct, IPbtNodePath<TOther> => PbtNodePathOperations.Equal(this, other);
    public override bool Equals(object? obj) => obj switch
    {
        PbtNodePath path => Equals(path),
        PbtStorageNodePath path => Equals(path),
        _ => false
    };
    public override int GetHashCode() => PbtNodePathOperations.Hash(_path.Bytes, BitDepth);
}
