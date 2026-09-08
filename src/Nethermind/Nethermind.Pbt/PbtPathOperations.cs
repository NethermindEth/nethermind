// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;

namespace Nethermind.Pbt;

internal static class PbtPathOperations
{
    internal static void Validate(ReadOnlySpan<byte> path, int bitDepth, int maximumDepth)
    {
        if ((uint)bitDepth > (uint)maximumDepth) throw new ArgumentOutOfRangeException(nameof(bitDepth));
        int byteLength = (bitDepth + 7) >> 3;
        if (path.Length != byteLength) throw new ArgumentException("Path length does not match the bit depth.", nameof(path));
        if (byteLength != 0 && (bitDepth & 7) != 0 && (path[^1] & (0xFF >> (bitDepth & 7))) != 0)
            throw new ArgumentException("Unused path bits must be zero.", nameof(path));
    }

    internal static byte[] Encode(IPbtNodePath path)
    {
        byte[] encoding = GC.AllocateUninitializedArray<byte>(4 + path.Path.Length);
        BinaryPrimitives.WriteUInt32BigEndian(encoding, (uint)path.BitDepth);
        path.Path.CopyTo(encoding.AsSpan(4));
        return encoding;
    }

    internal static TPath Decode<TPath>(ReadOnlySpan<byte> encoding) where TPath : class, IPbtNodePath<TPath>
    {
        if (encoding.Length < 4) throw new InvalidDataException("Truncated PBT node path.");
        uint depth = BinaryPrimitives.ReadUInt32BigEndian(encoding);
        if (depth > TPath.MaxBitDepth || encoding.Length != 4 + ((depth + 7) >> 3))
            throw new InvalidDataException("Invalid PBT node path length.");
        try { return TPath.Create(encoding[4..], (int)depth); }
        catch (ArgumentException exception) { throw new InvalidDataException("Invalid PBT node path padding.", exception); }
    }

    internal static IPbtNodePath Decode(ReadOnlySpan<byte> encoding)
    {
        if (encoding.Length < 4) throw new InvalidDataException("Truncated PBT node path.");
        return BinaryPrimitives.ReadUInt32BigEndian(encoding) <= PbtNodePath.MaxBitDepth
            ? Decode<PbtNodePath>(encoding) : Decode<PbtStorageNodePath>(encoding);
    }

    internal static IPbtNodePath Create(ReadOnlySpan<byte> path, int bitDepth) => bitDepth <= PbtNodePath.MaxBitDepth
        ? new PbtNodePath(path, bitDepth) : new PbtStorageNodePath(path, bitDepth);

    internal static TPath FromKey<TPath>(ReadOnlySpan<byte> key, int bitDepth) where TPath : class, IPbtNodePath<TPath>
    {
        if (key.IsEmpty) throw new ArgumentException("A complete key cannot be empty.", nameof(key));
        ArgumentOutOfRangeException.ThrowIfNegative(bitDepth);
        if (bitDepth > key.Length * 8 || bitDepth > TPath.MaxBitDepth) throw new ArgumentOutOfRangeException(nameof(bitDepth));
        Span<byte> path = stackalloc byte[(bitDepth + 7) >> 3];
        key[..path.Length].CopyTo(path);
        if (path.Length != 0 && (bitDepth & 7) != 0) path[^1] &= (byte)(0xFF << (8 - (bitDepth & 7)));
        return TPath.Create(path, bitDepth);
    }

    internal static TPath Append<TPath>(IPbtNodePath source, ReadOnlySpan<byte> prefix, int bitCount, int direction) where TPath : class, IPbtNodePath<TPath>
    {
        if ((uint)direction > 1) throw new ArgumentOutOfRangeException(nameof(direction));
        int depth = checked(source.BitDepth + bitCount + 1);
        if (depth > TPath.MaxBitDepth) throw new ArgumentOutOfRangeException(nameof(prefix));
        Span<byte> path = stackalloc byte[(depth + 7) >> 3];
        path.Clear();
        source.Path.CopyTo(path);
        PbtBitPrefix.CopyBits(prefix, 0, bitCount, path, source.BitDepth);
        if (direction != 0)
        {
            int bit = depth - 1;
            path[bit >> 3] |= (byte)(1 << (7 - (bit & 7)));
        }
        return TPath.Create(path, depth);
    }

    internal static int Compare(IPbtNodePath path, IPbtNodePath? other)
    {
        if (other is null) return 1;
        int depthComparison = path.BitDepth.CompareTo(other.BitDepth);
        return depthComparison != 0 ? depthComparison : path.Path.SequenceCompareTo(other.Path);
    }

    internal static bool Equal(IPbtNodePath path, IPbtNodePath? other) =>
        other is not null && path.BitDepth == other.BitDepth && path.Path.SequenceEqual(other.Path);

    internal static int Hash(IPbtNodePath path)
    {
        HashCode hash = new();
        hash.Add(path.BitDepth);
        hash.AddBytes(path.Path);
        return hash.ToHashCode();
    }
}
