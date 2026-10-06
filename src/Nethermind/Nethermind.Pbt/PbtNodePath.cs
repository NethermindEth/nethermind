// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Pbt;

/// <summary>Identifies a canonical tree node by its consumed MSB-first key path.</summary>
/// <remarks>Paths are limited to 272 bits.</remarks>
public readonly struct PbtNodePath : IPbtNodePath<PbtNodePath>, IEquatable<PbtNodePath>, IComparable<PbtNodePath>
{
    private readonly PbtPath _path;

    /// <inheritdoc/>
    public static int MaxBitDepth => PbtPath.KeyLength * 8;
    /// <inheritdoc/>
    public static PbtNodePath Create(ReadOnlySpan<byte> path, int bitDepth) => new(path, bitDepth, validated: true);

    public PbtNodePath(ReadOnlySpan<byte> path, int bitDepth) : this(path, bitDepth, validated: false) { }

    private PbtNodePath(ReadOnlySpan<byte> path, int bitDepth, bool validated)
    {
        if (!validated) PbtNodeCodec.ValidatePath(path, bitDepth, MaxBitDepth);
        _path = path.IsEmpty ? default : PbtPath.ZeroPad(path);
        BitDepth = bitDepth;
    }

    public int BitDepth { get; }
    [UnscopedRef]
    private ReadOnlySpan<byte> Bytes => _path.Bytes[..PbtBitPrefix.ByteCount(BitDepth)];
    /// <inheritdoc/>
    public byte GetByte(int byteIndex) => Bytes[byteIndex];

    /// <inheritdoc/>
    public PbtNodePath Prefix(int depth) => PbtNodePathOperations.Prefix<PbtNodePath>(Bytes, BitDepth, depth);

    /// <inheritdoc/>
    public PbtNodePath AppendBits(int bits, int bitCount) =>
        PbtNodePathOperations.AppendBits<PbtNodePath>(Bytes, BitDepth, bits, bitCount);
    /// <inheritdoc/>
    public TPath ToPath<TPath>() where TPath : struct, IPbtNodePath<TPath> => TPath.Create(Bytes, BitDepth);

    public int CompareTo(PbtNodePath other)
    {
        int depthComparison = BitDepth.CompareTo(other.BitDepth);
        return depthComparison != 0 ? depthComparison : Bytes.SequenceCompareTo(other.Bytes);
    }
    public bool Equals(PbtNodePath other) => BitDepth == other.BitDepth && Bytes.SequenceEqual(other.Bytes);
    public override bool Equals(object? obj) => obj switch
    {
        PbtNodePath path => Equals(path),
        PbtStorageNodePath path => PbtNodePathOperations.Equal(this, path),
        _ => false
    };
    public override int GetHashCode() => PbtNodePathOperations.Hash(Bytes, BitDepth);
}
