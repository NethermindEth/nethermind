// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;

namespace Nethermind.Pbt;

/// <summary>Identifies a canonical tree node by its consumed MSB-first key path.</summary>
/// <remarks>Paths are limited to 528 bits; keys larger than 66 bytes are unsupported.</remarks>
public sealed class PbtNodePath : IEquatable<PbtNodePath>, IComparable<PbtNodePath>
{
    private readonly PbtFullKey _path;

    public PbtNodePath(ReadOnlySpan<byte> path, int bitDepth)
    {
        if ((uint)bitDepth > PbtFullKey.MaxLength * 8U) throw new ArgumentOutOfRangeException(nameof(bitDepth));
        int byteLength = (bitDepth + 7) >> 3;
        if (path.Length != byteLength) throw new ArgumentException("Path length does not match the bit depth.", nameof(path));
        if (byteLength != 0 && (bitDepth & 7) != 0 && (path[^1] & (0xFF >> (bitDepth & 7))) != 0)
        {
            throw new ArgumentException("Unused path bits must be zero.", nameof(path));
        }
        _path = path.IsEmpty ? default : new PbtFullKey(path);
        BitDepth = bitDepth;
    }

    public int BitDepth { get; }
    public ReadOnlySpan<byte> Path => _path.Bytes;

    public byte[] Encode()
    {
        byte[] encoding = GC.AllocateUninitializedArray<byte>(4 + _path.Length);
        BinaryPrimitives.WriteUInt32BigEndian(encoding, (uint)BitDepth);
        Path.CopyTo(encoding.AsSpan(4));
        return encoding;
    }

    public static PbtNodePath Decode(ReadOnlySpan<byte> encoding)
    {
        if (encoding.Length < 4) throw new InvalidDataException("Truncated PBT node path.");
        uint depth = BinaryPrimitives.ReadUInt32BigEndian(encoding);
        if (depth > PbtFullKey.MaxLength * 8U || encoding.Length != 4 + ((depth + 7) >> 3))
        {
            throw new InvalidDataException("Invalid PBT node path length.");
        }
        try
        {
            return new PbtNodePath(encoding[4..], (int)depth);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Invalid PBT node path padding.", exception);
        }
    }

    internal static PbtNodePath FromKey(PbtFullKey key, int bitDepth)
    {
        if (key.Length == 0) throw new ArgumentException("A complete key cannot be empty.", nameof(key));
        ArgumentOutOfRangeException.ThrowIfNegative(bitDepth);
        if (bitDepth > key.BitLength) throw new ArgumentOutOfRangeException(nameof(bitDepth));
        Span<byte> path = stackalloc byte[(bitDepth + 7) >> 3];
        key.Bytes[..path.Length].CopyTo(path);
        if (path.Length != 0 && (bitDepth & 7) != 0) path[^1] &= (byte)(0xFF << (8 - (bitDepth & 7)));
        return new PbtNodePath(path, bitDepth);
    }

    internal PbtNodePath Append(PbtBitPrefix prefix, int direction) => Append(prefix.Bytes, prefix.BitCount, direction);

    internal PbtNodePath Append(ReadOnlySpan<byte> prefix, int bitCount, int direction)
    {
        if ((uint)direction > 1) throw new ArgumentOutOfRangeException(nameof(direction));
        int depth = checked(BitDepth + bitCount + 1);
        if (depth > PbtFullKey.MaxLength * 8) throw new ArgumentOutOfRangeException(nameof(prefix));
        Span<byte> path = stackalloc byte[(depth + 7) >> 3];
        path.Clear();
        Path.CopyTo(path);
        PbtBitPrefix.CopyBits(prefix, 0, bitCount, path, BitDepth);
        if (direction != 0)
        {
            int bit = depth - 1;
            path[bit >> 3] |= (byte)(1 << (7 - (bit & 7)));
        }
        return new PbtNodePath(path, depth);
    }

    public int CompareTo(PbtNodePath? other)
    {
        if (other is null) return 1;
        int depthComparison = BitDepth.CompareTo(other.BitDepth);
        return depthComparison != 0 ? depthComparison : Path.SequenceCompareTo(other.Path);
    }

    public bool Equals(PbtNodePath? other) => other is not null && BitDepth == other.BitDepth && Path.SequenceEqual(other.Path);
    public override bool Equals(object? obj) => obj is PbtNodePath other && Equals(other);
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(BitDepth);
        hash.AddBytes(Path);
        return hash.ToHashCode();
    }
}
