// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

internal static class PbtNodeCodec
{
    private const byte LeafTag = 0;
    private const byte BranchTag = 1;

    /// <summary>Validates the exact structural encoding of one node without allocating.</summary>
    internal static void ValidateExact(ReadOnlySpan<byte> encoding)
    {
        if (encoding.IsEmpty) throw new InvalidDataException("A PBT node encoding cannot be empty.");
        if (encoding[0] is not LeafTag and not BranchTag) throw new InvalidDataException("Unknown PBT node tag.");
        if (encoding.Length < 3) throw new InvalidDataException("Truncated PBT node encoding.");

        if (encoding[0] == LeafTag)
        {
            int keyLength = BinaryPrimitives.ReadUInt16BigEndian(encoding[1..]);
            if (keyLength is < 1 or > PbtFullKey.MaxLength)
                throw new InvalidDataException("Invalid PBT leaf key length.");
            int encodedLength = checked(3 + keyLength + 32);
            if (encoding.Length != encodedLength) throw new InvalidDataException("Invalid PBT leaf encoding length.");
            return;
        }

        int bitCount = BinaryPrimitives.ReadUInt16BigEndian(encoding[1..]);
        int prefixByteCount = PbtBitPrefix.ByteCount(bitCount);
        int encodedBranchLength = checked(3 + prefixByteCount + 64);
        if (encoding.Length != encodedBranchLength) throw new InvalidDataException("Invalid PBT branch encoding length.");
        if (bitCount % 8 != 0 && (encoding[2 + prefixByteCount] & (0xFF >> (bitCount % 8))) != 0)
            throw new InvalidDataException("Invalid PBT branch prefix.");

        int leftHashOffset = 3 + prefixByteCount;
        if (IsZero(encoding[leftHashOffset..(leftHashOffset + 32)])
            || IsZero(encoding[(leftHashOffset + 32)..(leftHashOffset + 64)]))
            throw new InvalidDataException("A PBT branch must have two non-empty children.");
    }

    private static bool IsZero(ReadOnlySpan<byte> bytes)
    {
        for (int index = 0; index < bytes.Length; index++)
            if (bytes[index] != 0) return false;
        return true;
    }

    internal static ValueHash256 Hash(PbtNodeReader node)
    {
        if (!node.IsLeaf) return Blake3Hash.Hash(node.Encoding);
        Span<byte> preimage = stackalloc byte[1 + node.Key.Length + 32];
        preimage[0] = LeafTag;
        node.Key.CopyTo(preimage[1..]);
        node.Value.CopyTo(preimage[(1 + node.Key.Length)..]);
        return Blake3Hash.Hash(preimage);
    }

    internal static byte[] EncodeLeaf(PbtFullKey key, ReadOnlySpan<byte> value)
    {
        if (key.Length == 0) throw new ArgumentException("A complete key cannot be empty.", nameof(key));
        if (value.Length != 32) throw new ArgumentException("Value must be exactly 32 bytes.", nameof(value));
        byte[] encoding = GC.AllocateUninitializedArray<byte>(3 + key.Length + 32);
        EncodeLeaf(encoding, key, value);
        return encoding;
    }

    internal static void EncodeLeaf(Span<byte> encoding, PbtFullKey key, ReadOnlySpan<byte> value)
    {
        if (key.Length == 0) throw new ArgumentException("A complete key cannot be empty.", nameof(key));
        if (value.Length != 32) throw new ArgumentException("Value must be exactly 32 bytes.", nameof(value));
        encoding[0] = LeafTag;
        BinaryPrimitives.WriteUInt16BigEndian(encoding[1..], (ushort)key.Length);
        key.Bytes.CopyTo(encoding[3..]);
        value.CopyTo(encoding[(3 + key.Length)..]);
    }

    internal static byte[] EncodeBranch(ReadOnlySpan<byte> prefix, int bitCount, in ValueHash256 left, in ValueHash256 right)
    {
        if ((uint)bitCount > PbtBitPrefix.MaxBitCount) throw new ArgumentOutOfRangeException(nameof(bitCount));
        if (prefix.Length != PbtBitPrefix.ByteCount(bitCount)) throw new ArgumentException("Prefix length does not match its bit count.", nameof(prefix));
        byte[] encoding = CreateBranchEncoding(bitCount, left, right);
        prefix.CopyTo(encoding.AsSpan(3));
        ValidateExact(encoding);
        return encoding;
    }

    /// <summary>Allocates a branch encoding with a zeroed prefix for direct bit composition.</summary>
    internal static byte[] CreateBranchEncoding(int bitCount, in ValueHash256 left, in ValueHash256 right)
    {
        if ((uint)bitCount > PbtBitPrefix.MaxBitCount) throw new ArgumentOutOfRangeException(nameof(bitCount));
        if (left == default || right == default) throw new InvalidDataException("A PBT branch must have two non-empty children.");
        int prefixLength = PbtBitPrefix.ByteCount(bitCount);
        byte[] encoding = new byte[3 + prefixLength + 64];
        CreateBranchEncoding(encoding, bitCount, left, right);
        return encoding;
    }

    internal static void CreateBranchEncoding(Span<byte> encoding, int bitCount, in ValueHash256 left, in ValueHash256 right)
    {
        if ((uint)bitCount > PbtBitPrefix.MaxBitCount) throw new ArgumentOutOfRangeException(nameof(bitCount));
        if (left == default || right == default) throw new InvalidDataException("A PBT branch must have two non-empty children.");
        int prefixLength = PbtBitPrefix.ByteCount(bitCount);
        encoding[0] = BranchTag;
        BinaryPrimitives.WriteUInt16BigEndian(encoding[1..], (ushort)bitCount);
        encoding.Slice(3, prefixLength).Clear();
        left.Bytes.CopyTo(encoding[(3 + prefixLength)..]);
        right.Bytes.CopyTo(encoding[(3 + prefixLength + 32)..]);
    }
}
