// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        IPbtNodePath<PbtNodePath>.Validate(path, bitDepth, MaxBitDepth);
        _path = path.IsEmpty ? default : new PbtFullKey(path);
        BitDepth = bitDepth;
    }

    public int BitDepth { get; }
    /// <inheritdoc/>
    public int GetBit(int bitIndex) => IPbtNodePath<PbtNodePath>.GetBit(_path.Bytes, BitDepth, bitIndex);
    /// <inheritdoc/>
    public byte GetByte(int byteIndex) => _path.Bytes[byteIndex];
    /// <inheritdoc/>
    public void CopyBitsTo(int sourceBitOffset, Span<byte> destination, int destinationBitOffset, int bitCount) =>
        IPbtNodePath<PbtNodePath>.CopyBitsTo(_path.Bytes, BitDepth, sourceBitOffset, destination, destinationBitOffset, bitCount);
    /// <inheritdoc/>
    public bool MatchesPrefix(ReadOnlySpan<byte> key, int bitCount) =>
        IPbtNodePath<PbtNodePath>.MatchesPrefix(_path.Bytes, BitDepth, key, bitCount);

    /// <inheritdoc/>
    public PbtNodePath Prefix(int depth) => IPbtNodePath<PbtNodePath>.Prefix(this, depth);

    /// <inheritdoc/>
    public PbtNodePath AppendNib(int nibble) => AppendBits(nibble, 4);

    /// <inheritdoc/>
    public PbtNodePath AppendBits(int bits, int bitCount) =>
        IPbtNodePath<PbtNodePath>.AppendBits<PbtNodePath>(_path.Bytes, BitDepth, bits, bitCount);
    /// <inheritdoc/>
    public TPath ToPath<TPath>() where TPath : struct, IPbtNodePath<TPath> => TPath.Create(_path.Bytes, BitDepth);
    /// <inheritdoc/>
    public bool MatchesPrefix<TOther>(TOther other, int bitCount) where TOther : struct, IPbtNodePath<TOther> =>
        IPbtNodePath<PbtNodePath>.MatchesPrefix(this, other, bitCount);

    /// <inheritdoc/>
    public int EncodedLength => 4 + ((BitDepth + 7) >> 3);
    /// <inheritdoc/>
    public void Encode(Span<byte> destination) => IPbtNodePath<PbtNodePath>.Encode(_path.Bytes, BitDepth, destination);
    public static PbtNodePath Decode(ReadOnlySpan<byte> encoding) => IPbtNodePath<PbtNodePath>.Decode<PbtNodePath>(encoding);

    internal static PbtNodePath FromKey(PbtFullKey key, int bitDepth) => IPbtNodePath<PbtNodePath>.FromKey<PbtNodePath>(key.Bytes, bitDepth);

    /// <inheritdoc/>
    public PbtNodePath Append(CompressedPrefix prefix, int direction) =>
        IPbtNodePath<PbtNodePath>.Append(this, prefix, direction);

    public int CompareTo(PbtNodePath other)
    {
        int depthComparison = BitDepth.CompareTo(other.BitDepth);
        return depthComparison != 0 ? depthComparison : _path.Bytes.SequenceCompareTo(other._path.Bytes);
    }
    public bool Equals(PbtNodePath other) => BitDepth == other.BitDepth && _path.Bytes.SequenceEqual(other._path.Bytes);
    public int CompareTo<TOther>(TOther other) where TOther : struct, IPbtNodePath<TOther> => IPbtNodePath<PbtNodePath>.Compare(this, other);
    public bool Equals<TOther>(TOther other) where TOther : struct, IPbtNodePath<TOther> => IPbtNodePath<PbtNodePath>.Equal(this, other);
    public override bool Equals(object? obj) => obj switch
    {
        PbtNodePath path => Equals(path),
        PbtStorageNodePath path => Equals(path),
        _ => false
    };
    public override int GetHashCode() => IPbtNodePath<PbtNodePath>.Hash(_path.Bytes, BitDepth);
}
