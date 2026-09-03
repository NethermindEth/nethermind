// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

internal abstract record PbtCanonicalNode
{
    internal abstract ValueHash256 Hash { get; }
}

internal sealed record PbtLeafNode(PbtFullKey Key, byte[] Value) : PbtCanonicalNode
{
    internal override ValueHash256 Hash => PbtNodeCodec.HashLeaf(Key, Value);
}

internal sealed record PbtBranchNode(PbtBitPrefix Prefix, ValueHash256 LeftHash, ValueHash256 RightHash) : PbtCanonicalNode
{
    internal override ValueHash256 Hash => PbtNodeCodec.HashBranch(Prefix, LeftHash, RightHash);
}

internal static class PbtNodeCodec
{
    private const byte LeafTag = 0;
    private const byte BranchTag = 1;

    public static byte[] Encode(PbtCanonicalNode node) => node switch
    {
        PbtLeafNode leaf => EncodeLeaf(leaf),
        PbtBranchNode branch => EncodeBranch(branch),
        _ => throw new ArgumentOutOfRangeException(nameof(node)),
    };

    public static PbtCanonicalNode Decode(ReadOnlySpan<byte> encoding)
    {
        if (encoding.IsEmpty) throw new InvalidDataException("A PBT node encoding cannot be empty.");
        return encoding[0] switch
        {
            LeafTag => DecodeLeaf(encoding),
            BranchTag => DecodeBranch(encoding),
            _ => throw new InvalidDataException("Unknown PBT node tag."),
        };
    }

    internal static ValueHash256 HashLeaf(PbtFullKey key, ReadOnlySpan<byte> value)
    {
        if (value.Length != 32) throw new ArgumentException("Value must be exactly 32 bytes.", nameof(value));
        byte[] preimage = GC.AllocateUninitializedArray<byte>(1 + key.Length + 32);
        preimage[0] = LeafTag;
        key.Bytes.CopyTo(preimage.AsSpan(1));
        value.CopyTo(preimage.AsSpan(1 + key.Length));
        return Blake3Hash.Hash(preimage);
    }

    internal static ValueHash256 HashBranch(PbtBitPrefix prefix, in ValueHash256 left, in ValueHash256 right)
    {
        int prefixByteCount = prefix.Bytes.Length;
        byte[] preimage = GC.AllocateUninitializedArray<byte>(3 + prefixByteCount + 64);
        preimage[0] = BranchTag;
        BinaryPrimitives.WriteUInt16BigEndian(preimage.AsSpan(1), (ushort)prefix.BitCount);
        prefix.Bytes.CopyTo(preimage.AsSpan(3));
        left.Bytes.CopyTo(preimage.AsSpan(3 + prefixByteCount));
        right.Bytes.CopyTo(preimage.AsSpan(3 + prefixByteCount + 32));
        return Blake3Hash.Hash(preimage);
    }

    private static byte[] EncodeLeaf(PbtLeafNode leaf)
    {
        byte[] encoding = GC.AllocateUninitializedArray<byte>(3 + leaf.Key.Length + 32);
        encoding[0] = LeafTag;
        BinaryPrimitives.WriteUInt16BigEndian(encoding.AsSpan(1), (ushort)leaf.Key.Length);
        leaf.Key.Bytes.CopyTo(encoding.AsSpan(3));
        leaf.Value.CopyTo(encoding, 3 + leaf.Key.Length);
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
            return new PbtLeafNode(new PbtFullKey(encoding.Slice(3, keyLength)), encoding[^32..].ToArray());
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
