// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nethermind.Pbt;

/// <summary>Identifies a canonical tree node by its consumed MSB-first key path.</summary>
/// <remarks>Paths are limited to 528 bits.</remarks>
public readonly struct PbtStorageNodePath : IPbtNodePath<PbtStorageNodePath>, IEquatable<PbtStorageNodePath>, IComparable<PbtStorageNodePath>
{
    private const int MaxByteLength = PbtVariableTreeKey.MaxLength;

    private readonly PathBytes _bytes;

    [InlineArray(MaxByteLength)]
    private struct PathBytes
    {
        private byte _element0;
    }

    /// <inheritdoc/>
    public static int MaxBitDepth => MaxByteLength * 8;
    /// <inheritdoc/>
    public static PbtStorageNodePath Create(ReadOnlySpan<byte> path, int bitDepth) => new(path, bitDepth, validated: true);

    public PbtStorageNodePath(ReadOnlySpan<byte> path, int bitDepth) : this(path, bitDepth, validated: false) { }

    private PbtStorageNodePath(ReadOnlySpan<byte> path, int bitDepth, bool validated)
    {
        if (!validated) PbtNodeCodec.ValidatePath(path, bitDepth, MaxBitDepth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(path.Length, MaxByteLength, nameof(path));
        path.CopyTo(_bytes);
        BitDepth = bitDepth;
    }

    public int BitDepth { get; }
    [UnscopedRef]
    private ReadOnlySpan<byte> Bytes => _bytes[..PbtBitPrefix.ByteCount(BitDepth)];
    /// <inheritdoc/>
    public byte GetByte(int byteIndex) => Bytes[byteIndex];

    /// <inheritdoc/>
    public TPath ToPath<TPath>() where TPath : struct, IPbtNodePath<TPath> => typeof(TPath) == typeof(PbtStorageNodePath)
        ? Unsafe.As<PbtStorageNodePath, TPath>(ref Unsafe.AsRef(in this))
        : TPath.Create(Bytes, BitDepth);

    public int CompareTo(PbtStorageNodePath other)
    {
        int depthComparison = BitDepth.CompareTo(other.BitDepth);
        return depthComparison != 0 ? depthComparison : Bytes.SequenceCompareTo(other.Bytes);
    }
    public bool Equals(PbtStorageNodePath other) => BitDepth == other.BitDepth && Bytes.SequenceEqual(other.Bytes);
    public override bool Equals(object? obj) => obj is PbtStorageNodePath path && Equals(path);
    public override int GetHashCode() => PbtNodePathOperations.Hash(Bytes, BitDepth);
}
