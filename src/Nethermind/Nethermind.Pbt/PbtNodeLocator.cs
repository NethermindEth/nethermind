// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;

namespace Nethermind.Pbt;

/// <summary>Identifies a canonical tree node by its consumed MSB-first key path.</summary>
internal sealed class PbtNodeLocator : IEquatable<PbtNodeLocator>
{
    private readonly byte[] _path;

    public PbtNodeLocator(ReadOnlySpan<byte> path, int bitDepth)
    {
        if ((uint)bitDepth > PbtFullKey.MaxLength * 8U) throw new ArgumentOutOfRangeException(nameof(bitDepth));
        int byteLength = (bitDepth + 7) >> 3;
        if (path.Length != byteLength) throw new ArgumentException("Path length does not match the bit depth.", nameof(path));
        if (byteLength != 0 && (bitDepth & 7) != 0 && (path[^1] & (0xFF >> (bitDepth & 7))) != 0)
        {
            throw new ArgumentException("Unused path bits must be zero.", nameof(path));
        }
        _path = path.ToArray();
        BitDepth = bitDepth;
    }

    public int BitDepth { get; }
    public ReadOnlySpan<byte> Path => _path;

    public byte[] Encode()
    {
        byte[] encoding = GC.AllocateUninitializedArray<byte>(4 + _path.Length);
        BinaryPrimitives.WriteUInt32BigEndian(encoding, (uint)BitDepth);
        _path.CopyTo(encoding, 4);
        return encoding;
    }

    public static PbtNodeLocator Decode(ReadOnlySpan<byte> encoding)
    {
        if (encoding.Length < 4) throw new InvalidDataException("Truncated PBT node locator.");
        uint depth = BinaryPrimitives.ReadUInt32BigEndian(encoding);
        if (depth > PbtFullKey.MaxLength * 8U || encoding.Length != 4 + ((depth + 7) >> 3))
        {
            throw new InvalidDataException("Invalid PBT node locator length.");
        }
        try
        {
            return new PbtNodeLocator(encoding[4..], (int)depth);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Invalid PBT node locator padding.", exception);
        }
    }

    internal static PbtNodeLocator FromKey(PbtFullKey key, int bitDepth)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bitDepth);
        if (bitDepth > key.BitLength) throw new ArgumentOutOfRangeException(nameof(bitDepth));
        byte[] path = new byte[(bitDepth + 7) >> 3];
        key.Bytes[..path.Length].CopyTo(path);
        if (path.Length != 0 && (bitDepth & 7) != 0) path[^1] &= (byte)(0xFF << (8 - (bitDepth & 7)));
        return new PbtNodeLocator(path, bitDepth);
    }

    internal PbtNodeLocator Append(PbtBitPrefix prefix, int direction)
    {
        if ((uint)direction > 1) throw new ArgumentOutOfRangeException(nameof(direction));
        int depth = checked(BitDepth + prefix.BitCount + 1);
        byte[] path = new byte[(depth + 7) >> 3];
        _path.CopyTo(path, 0);
        for (int i = 0; i < prefix.BitCount; i++)
        {
            if (prefix.GetBit(i) == 0) continue;
            int bit = BitDepth + i;
            path[bit >> 3] |= (byte)(1 << (7 - (bit & 7)));
        }
        if (direction != 0)
        {
            int bit = depth - 1;
            path[bit >> 3] |= (byte)(1 << (7 - (bit & 7)));
        }
        return new PbtNodeLocator(path, depth);
    }

    public bool Equals(PbtNodeLocator? other) => other is not null && BitDepth == other.BitDepth && Path.SequenceEqual(other.Path);
    public override bool Equals(object? obj) => obj is PbtNodeLocator other && Equals(other);
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(BitDepth);
        hash.AddBytes(_path);
        return hash.ToHashCode();
    }
}
