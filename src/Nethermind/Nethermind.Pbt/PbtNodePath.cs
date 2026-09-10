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
        IPbtNodePath.Validate(path, bitDepth, MaxBitDepth);
        _path = path.IsEmpty ? default : new PbtFullKey(path);
        BitDepth = bitDepth;
    }

    public int BitDepth { get; }
    /// <inheritdoc/>
    public int GetBit(int bitIndex) => IPbtNodePath.GetBit(_path.Bytes, BitDepth, bitIndex);
    /// <inheritdoc/>
    public byte GetByte(int byteIndex) => _path.Bytes[byteIndex];
    /// <inheritdoc/>
    public void CopyBitsTo(int sourceBitOffset, Span<byte> destination, int destinationBitOffset, int bitCount) =>
        IPbtNodePath.CopyBitsTo(_path.Bytes, BitDepth, sourceBitOffset, destination, destinationBitOffset, bitCount);
    /// <inheritdoc/>
    public bool MatchesPrefix(ReadOnlySpan<byte> key, int bitCount) =>
        IPbtNodePath.MatchesPrefix(_path.Bytes, BitDepth, key, bitCount);

    /// <inheritdoc/>
    public IPbtNodePath AppendNib(int nibble) => AppendBits(nibble, 4);

    /// <inheritdoc/>
    public IPbtNodePath AppendBits(int bits, int bitCount) => IPbtNodePath.AppendBits(_path.Bytes, BitDepth, bits, bitCount);
    /// <inheritdoc/>
    public TPath AppendBits<TPath>(int bits, int bitCount) where TPath : struct, IPbtNodePath<TPath> =>
        IPbtNodePath.AppendBits<TPath>(_path.Bytes, BitDepth, bits, bitCount);
    /// <inheritdoc/>
    public TPath ToPath<TPath>() where TPath : struct, IPbtNodePath<TPath> => TPath.Create(_path.Bytes, BitDepth);
    /// <inheritdoc/>
    public bool MatchesPrefix<TOther>(TOther other, int bitCount) where TOther : IPbtNodePath =>
        IPbtNodePath.MatchesPrefix(this, other, bitCount);

    /// <inheritdoc/>
    public int EncodedLength => 4 + ((BitDepth + 7) >> 3);
    /// <inheritdoc/>
    public void Encode(Span<byte> destination) => IPbtNodePath.Encode(_path.Bytes, BitDepth, destination);
    public static PbtNodePath Decode(ReadOnlySpan<byte> encoding) => IPbtNodePath.Decode<PbtNodePath>(encoding);

    internal static PbtNodePath FromKey(PbtFullKey key, int bitDepth) => IPbtNodePath.FromKey<PbtNodePath>(key.Bytes, bitDepth);

    internal PbtNodePath Append(PbtBitPrefix prefix, int direction) => Append(prefix.Bytes, prefix.BitCount, direction);
    internal PbtNodePath Append(ReadOnlySpan<byte> prefix, int bitCount, int direction) =>
        IPbtNodePath.Append<PbtNodePath>(this, prefix, bitCount, direction);

    public int CompareTo(PbtNodePath other)
    {
        int depthComparison = BitDepth.CompareTo(other.BitDepth);
        return depthComparison != 0 ? depthComparison : _path.Bytes.SequenceCompareTo(other._path.Bytes);
    }
    public bool Equals(PbtNodePath other) => BitDepth == other.BitDepth && _path.Bytes.SequenceEqual(other._path.Bytes);
    public int CompareTo(IPbtNodePath? other) => IPbtNodePath.Compare(this, other);
    public bool Equals(IPbtNodePath? other) => IPbtNodePath.Equal(this, other);
    public override bool Equals(object? obj) => obj is IPbtNodePath other && Equals(other);
    public override int GetHashCode() => IPbtNodePath.Hash(_path.Bytes, BitDepth);
}
