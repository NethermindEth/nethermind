// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Pbt;

/// <summary>Identifies a canonical tree node by its consumed MSB-first key path.</summary>
/// <remarks>Paths are limited to 528 bits.</remarks>
public readonly struct PbtStorageNodePath : IPbtNodePath<PbtStorageNodePath>, IEquatable<PbtStorageNodePath>, IComparable<PbtStorageNodePath>
{
    private readonly PbtStorageTreeKey _path;
    private readonly int _hashCode;

    /// <inheritdoc/>
    public static int MaxBitDepth => PbtStorageTreeKey.MaxLength * 8;
    /// <inheritdoc/>
    public static PbtStorageNodePath Create(ReadOnlySpan<byte> path, int bitDepth) => new(path, bitDepth, validated: true);

    public PbtStorageNodePath(ReadOnlySpan<byte> path, int bitDepth) : this(path, bitDepth, validated: false) { }

    private PbtStorageNodePath(ReadOnlySpan<byte> path, int bitDepth, bool validated)
    {
        if (!validated) PbtNodeCodec.ValidatePath(path, bitDepth, MaxBitDepth);
        _path = path.IsEmpty ? default : new PbtStorageTreeKey(path);
        BitDepth = bitDepth;
        _hashCode = PbtNodePathOperations.Hash(path, bitDepth);
    }

    public int BitDepth { get; }
    /// <inheritdoc/>
    public byte GetByte(int byteIndex) => _path.Bytes[byteIndex];

    /// <inheritdoc/>
    public TPath ToPath<TPath>() where TPath : struct, IPbtNodePath<TPath> => typeof(TPath) == typeof(PbtStorageNodePath)
        ? Unsafe.As<PbtStorageNodePath, TPath>(ref Unsafe.AsRef(in this))
        : TPath.Create(_path.Bytes, BitDepth);

    public int CompareTo(PbtStorageNodePath other)
    {
        int depthComparison = BitDepth.CompareTo(other.BitDepth);
        return depthComparison != 0 ? depthComparison : _path.Bytes.SequenceCompareTo(other._path.Bytes);
    }
    public bool Equals(PbtStorageNodePath other) => BitDepth == other.BitDepth && _path.Bytes.SequenceEqual(other._path.Bytes);
    public override bool Equals(object? obj) => obj is PbtStorageNodePath path && Equals(path);
    public override int GetHashCode() => _hashCode;
}
