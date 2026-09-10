// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>Identifies a canonical tree node by its consumed MSB-first key path.</summary>
/// <remarks>Paths are limited to 528 bits.</remarks>
public readonly struct PbtStorageNodePath : IPbtNodePath<PbtStorageNodePath>, IEquatable<PbtStorageNodePath>, IComparable<PbtStorageNodePath>
{
    private readonly PbtStorageFullKey _path;

    /// <inheritdoc/>
    public static int MaxBitDepth => PbtStorageFullKey.MaxLength * 8;
    /// <inheritdoc/>
    public static PbtStorageNodePath Create(ReadOnlySpan<byte> path, int bitDepth) => new(path, bitDepth);

    public PbtStorageNodePath(ReadOnlySpan<byte> path, int bitDepth)
    {
        IPbtNodePath<PbtStorageNodePath>.Validate(path, bitDepth, MaxBitDepth);
        _path = path.IsEmpty ? default : new PbtStorageFullKey(path);
        BitDepth = bitDepth;
    }

    public int BitDepth { get; }
    /// <inheritdoc/>
    public int GetBit(int bitIndex) => IPbtNodePath<PbtStorageNodePath>.GetBit(_path.Bytes, BitDepth, bitIndex);
    /// <inheritdoc/>
    public byte GetByte(int byteIndex) => _path.Bytes[byteIndex];
    /// <inheritdoc/>
    public void CopyBitsTo(int sourceBitOffset, Span<byte> destination, int destinationBitOffset, int bitCount) =>
        IPbtNodePath<PbtStorageNodePath>.CopyBitsTo(_path.Bytes, BitDepth, sourceBitOffset, destination, destinationBitOffset, bitCount);
    /// <inheritdoc/>
    public bool MatchesPrefix(ReadOnlySpan<byte> key, int bitCount) =>
        IPbtNodePath<PbtStorageNodePath>.MatchesPrefix(_path.Bytes, BitDepth, key, bitCount);

    /// <inheritdoc/>
    public PbtStorageNodePath Prefix(int depth) => IPbtNodePath<PbtStorageNodePath>.Prefix(this, depth);

    /// <inheritdoc/>
    public PbtStorageNodePath AppendNib(int nibble) => AppendBits(nibble, 4);

    /// <inheritdoc/>
    public PbtStorageNodePath AppendBits(int bits, int bitCount) =>
        IPbtNodePath<PbtStorageNodePath>.AppendBits<PbtStorageNodePath>(_path.Bytes, BitDepth, bits, bitCount);
    /// <inheritdoc/>
    public TPath ToPath<TPath>() where TPath : struct, IPbtNodePath<TPath> => TPath.Create(_path.Bytes, BitDepth);
    /// <inheritdoc/>
    public bool MatchesPrefix<TOther>(TOther other, int bitCount) where TOther : struct, IPbtNodePath<TOther> =>
        IPbtNodePath<PbtStorageNodePath>.MatchesPrefix(this, other, bitCount);

    /// <inheritdoc/>
    public int EncodedLength => 4 + ((BitDepth + 7) >> 3);
    /// <inheritdoc/>
    public void Encode(Span<byte> destination) => IPbtNodePath<PbtStorageNodePath>.Encode(_path.Bytes, BitDepth, destination);
    public static PbtStorageNodePath Decode(ReadOnlySpan<byte> encoding) => IPbtNodePath<PbtStorageNodePath>.Decode<PbtStorageNodePath>(encoding);

    internal static PbtStorageNodePath FromKey(PbtStorageFullKey key, int bitDepth) => IPbtNodePath<PbtStorageNodePath>.FromKey<PbtStorageNodePath>(key.Bytes, bitDepth);

    internal PbtStorageNodePath Append(PbtBitPrefix prefix, int direction) => Append(prefix.Bytes, prefix.BitCount, direction);
    internal PbtStorageNodePath Append(ReadOnlySpan<byte> prefix, int bitCount, int direction) =>
        IPbtNodePath<PbtStorageNodePath>.Append<PbtStorageNodePath>(this, prefix, bitCount, direction);

    public int CompareTo(PbtStorageNodePath other)
    {
        int depthComparison = BitDepth.CompareTo(other.BitDepth);
        return depthComparison != 0 ? depthComparison : _path.Bytes.SequenceCompareTo(other._path.Bytes);
    }
    public bool Equals(PbtStorageNodePath other) => BitDepth == other.BitDepth && _path.Bytes.SequenceEqual(other._path.Bytes);
    public int CompareTo<TOther>(TOther other) where TOther : struct, IPbtNodePath<TOther> => IPbtNodePath<PbtStorageNodePath>.Compare(this, other);
    public bool Equals<TOther>(TOther other) where TOther : struct, IPbtNodePath<TOther> => IPbtNodePath<PbtStorageNodePath>.Equal(this, other);
    public override bool Equals(object? obj) => obj switch
    {
        PbtNodePath path => Equals(path),
        PbtStorageNodePath path => Equals(path),
        _ => false
    };
    public override int GetHashCode() => IPbtNodePath<PbtStorageNodePath>.Hash(_path.Bytes, BitDepth);
}
