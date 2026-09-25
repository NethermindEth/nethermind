// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>Encodes the stored node layouts.</summary>
/// <remarks>
/// A branch is its EIP-8297 hash preimage, <c>[0x01][bitCount u16 BE][prefix][left hash][right hash]</c>, followed by a
/// trailer <c>[leftKeyLength u8][rightKeyLength u8][leftKey][rightKey]</c>. A non-zero key length declares that child a
/// leaf and inlines its complete key; its hash is the child hash already in the preimage, so leaves are not stored as
/// nodes. The only exception is a tree consisting of one leaf, whose root is stored as <c>[0x00][keyLength u8][key]</c>
/// without a value: its hash is the tree root, which every reader of the root group already has.
/// </remarks>
internal static class PbtNodeCodec
{
    private const byte LeafTag = 0;
    private const byte BranchTag = 1;
    internal const int BranchTrailerHeaderLength = 2;

    /// <summary>The length of a branch's hash preimage, which starts its encoding.</summary>
    internal static int BranchPreimageLength(int bitCount) => 3 + PbtBitPrefix.ByteCount(bitCount) + 64;

    /// <summary>The longest branch preimage a tree can hold: a prefix is bounded by the longest complete key.</summary>
    internal const int MaxBranchPreimageLength = 3 + PbtStorageTreeKey.MaxLength + 64;

    /// <summary>The length of a complete branch encoding.</summary>
    internal static int BranchLength(int bitCount, int leftKeyLength, int rightKeyLength) =>
        BranchPreimageLength(bitCount) + BranchTrailerHeaderLength + leftKeyLength + rightKeyLength;

    /// <summary>The length of a root leaf encoding.</summary>
    internal static int LeafLength(int keyLength) => 2 + keyLength;

    /// <summary>Validates the exact structural encoding of one node without allocating.</summary>
    internal static void ValidateExact(ReadOnlySpan<byte> encoding)
    {
        if (encoding.IsEmpty) throw new InvalidDataException("A PBT node encoding cannot be empty.");
        if (encoding[0] is not LeafTag and not BranchTag) throw new InvalidDataException("Unknown PBT node tag.");
        if (encoding.Length < 3) throw new InvalidDataException("Truncated PBT node encoding.");

        if (encoding[0] == LeafTag)
        {
            int keyLength = encoding[1];
            if (keyLength is < 1 or > PbtStorageTreeKey.MaxLength)
                throw new InvalidDataException("Invalid PBT leaf key length.");
            if (encoding.Length != LeafLength(keyLength)) throw new InvalidDataException("Invalid PBT leaf encoding length.");
            return;
        }

        int bitCount = BinaryPrimitives.ReadUInt16BigEndian(encoding[1..]);
        int prefixByteCount = PbtBitPrefix.ByteCount(bitCount);
        int preimageLength = BranchPreimageLength(bitCount);
        if (encoding.Length < preimageLength + BranchTrailerHeaderLength) throw new InvalidDataException("Invalid PBT branch encoding length.");
        int leftKeyLength = encoding[preimageLength];
        int rightKeyLength = encoding[preimageLength + 1];
        if (leftKeyLength > PbtStorageTreeKey.MaxLength || rightKeyLength > PbtStorageTreeKey.MaxLength)
            throw new InvalidDataException("Invalid PBT branch leaf key length.");
        if (encoding.Length != BranchLength(bitCount, leftKeyLength, rightKeyLength)) throw new InvalidDataException("Invalid PBT branch encoding length.");
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

    /// <summary>Hashes a branch. A root leaf's hash is the tree root and cannot be derived from its encoding.</summary>
    internal static ValueHash256 Hash(PbtNodeReader node)
    {
        if (node.IsLeaf) throw new InvalidOperationException("A PBT root leaf's hash is the tree root.");
        return Blake3Hash.Hash(node.Preimage);
    }

    /// <summary>Hashes a leaf from its complete key and 32-byte value, per EIP-8297.</summary>
    internal static ValueHash256 HashLeaf(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        if (value.Length != 32) throw new ArgumentException("Value must be exactly 32 bytes.", nameof(value));
        Span<byte> preimage = stackalloc byte[LeafPreimageLength(key.Length)];
        WriteLeafPreimage(preimage, key, value);
        return Blake3Hash.Hash(preimage);
    }

    /// <summary>The length of the preimage <see cref="HashLeaf"/> hashes for a key of <paramref name="keyLength"/> bytes.</summary>
    internal static int LeafPreimageLength(int keyLength) => 1 + keyLength + 32;

    /// <summary>Writes the preimage <see cref="HashLeaf"/> hashes into <paramref name="preimage"/>, which is <see cref="LeafPreimageLength"/> bytes long.</summary>
    internal static void WriteLeafPreimage(Span<byte> preimage, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        preimage[0] = LeafTag;
        key.CopyTo(preimage[1..]);
        value.CopyTo(preimage[(1 + key.Length)..]);
    }

    internal static byte[] EncodeLeaf<TKey>(TKey key) where TKey : struct, IPbtKey<TKey>
    {
        if (key.Length == 0) throw new ArgumentException("A complete key cannot be empty.", nameof(key));
        byte[] encoding = GC.AllocateUninitializedArray<byte>(LeafLength(key.Length));
        EncodeLeaf(encoding, key);
        return encoding;
    }

    internal static void EncodeLeaf<TKey>(Span<byte> encoding, TKey key) where TKey : struct, IPbtKey<TKey>
    {
        if (key.Length == 0) throw new ArgumentException("A complete key cannot be empty.", nameof(key));
        encoding[0] = LeafTag;
        encoding[1] = (byte)key.Length;
        key.Bytes.CopyTo(encoding[2..]);
    }

    /// <summary>Writes a branch's hash preimage with a zeroed prefix for direct bit composition.</summary>
    /// <remarks>Only the first <see cref="BranchPreimageLength"/> bytes are written; the trailer follows through <see cref="WriteBranchTrailer"/>.</remarks>
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

    /// <summary>Writes the inline leaf keys that follow a branch's preimage; an empty key declares a branch child.</summary>
    internal static void WriteBranchTrailer(Span<byte> trailer, ReadOnlySpan<byte> leftKey, ReadOnlySpan<byte> rightKey)
    {
        WriteBranchTrailer(trailer, leftKey.Length, rightKey.Length);
        leftKey.CopyTo(trailer[BranchTrailerHeaderLength..]);
        rightKey.CopyTo(trailer[(BranchTrailerHeaderLength + leftKey.Length)..]);
    }

    /// <summary>Writes the trailer's key lengths; the caller copies the keys behind them.</summary>
    internal static void WriteBranchTrailer(Span<byte> trailer, int leftKeyLength, int rightKeyLength)
    {
        if (leftKeyLength > PbtStorageTreeKey.MaxLength || rightKeyLength > PbtStorageTreeKey.MaxLength)
            throw new ArgumentException("An inline leaf key exceeds the maximum key length.");
        trailer[0] = (byte)leftKeyLength;
        trailer[1] = (byte)rightKeyLength;
    }
}
