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
        PbtPathOperations.Validate(path, bitDepth, MaxBitDepth);
        _path = path.IsEmpty ? default : new PbtFullKey(path);
        BitDepth = bitDepth;
    }

    public int BitDepth { get; }
    /// <inheritdoc/>
    public int GetBit(int bitIndex) => PbtPathOperations.GetBit(_path.Bytes, BitDepth, bitIndex);
    /// <inheritdoc/>
    public byte GetByte(int byteIndex) => _path.Bytes[byteIndex];
    /// <inheritdoc/>
    public void CopyBitsTo(int sourceBitOffset, Span<byte> destination, int destinationBitOffset, int bitCount) =>
        PbtPathOperations.CopyBitsTo(_path.Bytes, BitDepth, sourceBitOffset, destination, destinationBitOffset, bitCount);
    /// <inheritdoc/>
    public bool MatchesPrefix(ReadOnlySpan<byte> key, int bitCount) =>
        PbtPathOperations.MatchesPrefix(_path.Bytes, BitDepth, key, bitCount);

    /// <inheritdoc/>
    public IPbtNodePath AppendNib(int nibble) => AppendBits(nibble, 4);

    /// <inheritdoc/>
    public IPbtNodePath AppendBits(int bits, int bitCount) => PbtPathOperations.AppendBits(_path.Bytes, BitDepth, bits, bitCount);
    /// <inheritdoc/>
    public TPath AppendBits<TPath>(int bits, int bitCount) where TPath : struct, IPbtNodePath<TPath> =>
        PbtPathOperations.AppendBits<TPath>(_path.Bytes, BitDepth, bits, bitCount);
    /// <inheritdoc/>
    public TPath ToPath<TPath>() where TPath : struct, IPbtNodePath<TPath> => TPath.Create(_path.Bytes, BitDepth);
    /// <inheritdoc/>
    public bool MatchesPrefix<TOther>(TOther other, int bitCount) where TOther : IPbtNodePath =>
        PbtPathOperations.MatchesPrefix(this, other, bitCount);

    /// <inheritdoc/>
    public int EncodedLength => 4 + ((BitDepth + 7) >> 3);
    /// <inheritdoc/>
    public void Write(Span<byte> destination) => PbtPathOperations.Write(_path.Bytes, BitDepth, destination);
    public static PbtNodePath Decode(ReadOnlySpan<byte> encoding) => PbtPathOperations.Decode<PbtNodePath>(encoding);

    internal static PbtNodePath FromKey(PbtFullKey key, int bitDepth) => PbtPathOperations.FromKey<PbtNodePath>(key.Bytes, bitDepth);

    internal PbtNodePath Append(PbtBitPrefix prefix, int direction) => Append(prefix.Bytes, prefix.BitCount, direction);
    internal PbtNodePath Append(ReadOnlySpan<byte> prefix, int bitCount, int direction) =>
        PbtPathOperations.Append<PbtNodePath>(this, prefix, bitCount, direction);

    public int CompareTo(PbtNodePath other)
    {
        int depthComparison = BitDepth.CompareTo(other.BitDepth);
        return depthComparison != 0 ? depthComparison : _path.Bytes.SequenceCompareTo(other._path.Bytes);
    }
    public bool Equals(PbtNodePath other) => BitDepth == other.BitDepth && _path.Bytes.SequenceEqual(other._path.Bytes);
    public int CompareTo(IPbtNodePath? other) => PbtPathOperations.Compare(this, other);
    public bool Equals(IPbtNodePath? other) => PbtPathOperations.Equal(this, other);
    public override bool Equals(object? obj) => obj is IPbtNodePath other && Equals(other);
    public override int GetHashCode() => PbtPathOperations.Hash(_path.Bytes, BitDepth);
}
