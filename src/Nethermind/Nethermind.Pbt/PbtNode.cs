// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

internal abstract record PbtNode
{
    internal abstract ValueHash256 Hash { get; }
}

internal sealed record PbtLeafNode(PbtFullKey Key, ValueHash256 Value) : PbtNode
{
    internal PbtLeafNode(PbtFullKey key, ReadOnlySpan<byte> value) : this(key, new ValueHash256(value)) { }

    internal override ValueHash256 Hash { get; } = PbtNodeCodec.HashLeaf(Key, Value.Bytes);
}

internal sealed record PbtBranchNode(PbtBitPrefix Prefix, ValueHash256 LeftHash, ValueHash256 RightHash) : PbtNode
{
    internal override ValueHash256 Hash { get; } = PbtNodeCodec.HashBranch(Prefix, LeftHash, RightHash);
}

internal static class PbtNodeCodec
{
    private const byte LeafTag = 0;
    private const byte BranchTag = 1;
    private const int MaxStackHashPreimageLength = 256;

    public static byte[] Encode(PbtNode node) => node switch
    {
        PbtLeafNode leaf => EncodeLeaf(leaf),
        PbtBranchNode branch => EncodeBranch(branch),
        _ => throw new ArgumentOutOfRangeException(nameof(node)),
    };

    public static PbtNode Decode(ReadOnlySpan<byte> encoding)
    {
        ValidateExact(encoding);
        return encoding[0] switch
        {
            LeafTag => DecodeLeaf(encoding),
            BranchTag => DecodeBranch(encoding),
            _ => throw new InvalidDataException("Unknown PBT node tag."),
        };
    }

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

    internal static ValueHash256 HashLeaf(PbtFullKey key, ReadOnlySpan<byte> value)
    {
        if (key.Length == 0) throw new ArgumentException("A complete key cannot be empty.", nameof(key));
        if (value.Length != 32) throw new ArgumentException("Value must be exactly 32 bytes.", nameof(value));
        int preimageLength = 1 + key.Length + 32;
        Span<byte> preimage = preimageLength <= MaxStackHashPreimageLength
            ? stackalloc byte[preimageLength]
            : GC.AllocateUninitializedArray<byte>(preimageLength);
        preimage[0] = LeafTag;
        key.Bytes.CopyTo(preimage[1..]);
        value.CopyTo(preimage[(1 + key.Length)..]);
        return Blake3Hash.Hash(preimage);
    }

    internal static ValueHash256 HashBranch(PbtBitPrefix prefix, in ValueHash256 left, in ValueHash256 right)
    {
        int prefixByteCount = prefix.Bytes.Length;
        int preimageLength = 3 + prefixByteCount + 64;
        Span<byte> preimage = preimageLength <= MaxStackHashPreimageLength
            ? stackalloc byte[preimageLength]
            : GC.AllocateUninitializedArray<byte>(preimageLength);
        preimage[0] = BranchTag;
        BinaryPrimitives.WriteUInt16BigEndian(preimage[1..], (ushort)prefix.BitCount);
        prefix.Bytes.CopyTo(preimage[3..]);
        left.Bytes.CopyTo(preimage[(3 + prefixByteCount)..]);
        right.Bytes.CopyTo(preimage[(3 + prefixByteCount + 32)..]);
        return Blake3Hash.Hash(preimage);
    }

    private static byte[] EncodeLeaf(PbtLeafNode leaf)
    {
        byte[] encoding = GC.AllocateUninitializedArray<byte>(3 + leaf.Key.Length + 32);
        encoding[0] = LeafTag;
        BinaryPrimitives.WriteUInt16BigEndian(encoding.AsSpan(1), (ushort)leaf.Key.Length);
        leaf.Key.Bytes.CopyTo(encoding.AsSpan(3));
        leaf.Value.Bytes.CopyTo(encoding.AsSpan(3 + leaf.Key.Length));
        return encoding;
    }

    private static byte[] EncodeBranch(PbtBranchNode branch)
    {
        byte[] encoding = GC.AllocateUninitializedArray<byte>(3 + branch.Prefix.Bytes.Length + 64);
        encoding[0] = BranchTag;
        BinaryPrimitives.WriteUInt16BigEndian(encoding.AsSpan(1), (ushort)branch.Prefix.BitCount);
        branch.Prefix.Bytes.CopyTo(encoding.AsSpan(3));
        branch.LeftHash.Bytes.CopyTo(encoding.AsSpan(3 + branch.Prefix.Bytes.Length));
        branch.RightHash.Bytes.CopyTo(encoding.AsSpan(3 + branch.Prefix.Bytes.Length + 32));
        return encoding;
    }

    private static PbtLeafNode DecodeLeaf(ReadOnlySpan<byte> encoding)
    {
        if (encoding.Length < 3) throw new InvalidDataException("Truncated PBT leaf encoding.");
        int keyLength = BinaryPrimitives.ReadUInt16BigEndian(encoding[1..]);
        if (encoding.Length != 3 + keyLength + 32) throw new InvalidDataException("Invalid PBT leaf encoding length.");
        try
        {
            return new PbtLeafNode(new PbtFullKey(encoding.Slice(3, keyLength)), new ValueHash256(encoding[^32..]));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException("Invalid PBT leaf key length.", exception);
        }
    }

    private static PbtBranchNode DecodeBranch(ReadOnlySpan<byte> encoding)
    {
        if (encoding.Length < 3) throw new InvalidDataException("Truncated PBT branch encoding.");
        int bitCount = BinaryPrimitives.ReadUInt16BigEndian(encoding[1..]);
        int prefixByteCount = PbtBitPrefix.ByteCount(bitCount);
        if (encoding.Length != 3 + prefixByteCount + 64) throw new InvalidDataException("Invalid PBT branch encoding length.");
        PbtBitPrefix prefix;
        try
        {
            prefix = new PbtBitPrefix(encoding.Slice(3, prefixByteCount), bitCount);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Invalid PBT branch prefix.", exception);
        }
        ValueHash256 left = new(encoding.Slice(3 + prefixByteCount, 32));
        ValueHash256 right = new(encoding.Slice(3 + prefixByteCount + 32, 32));
        if (left == default || right == default) throw new InvalidDataException("A PBT branch must have two non-empty children.");
        return new PbtBranchNode(prefix, left, right);
    }
}
