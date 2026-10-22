// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Trie.NodeBuffer;

internal enum NodeRecordKind : byte
{
    Leaf,
    Extension,

    /// <summary>A branch whose every non-empty child is referenced by hash.</summary>
    HashedBranch,

    /// <summary>A branch with at least one embedded child.</summary>
    InlineBranch,

    /// <summary>A buffer-only record indexing a trie root; never a node.</summary>
    RootKey,
}

/// <summary>The byte layout of a trie node record, shared by the trie node buffer and warmed nodes.</summary>
/// <remarks>
/// All fields are little-endian:
/// <code>
/// 0   1   kind (<see cref="NodeRecordKind"/>)
/// 1   1   0
/// 2   2   hash mask: bit i set when child i is referenced by hash (an extension's child is bit 0)
/// 4   4   RLP length
/// 8   32  keccak256 of the RLP
/// 40  8   node key hash, <see cref="NodeKeyHash"/>
/// 48  8 * popcount(mask)  buffer pointers of the hash children, ascending child index, 0 when unknown
/// ..  RLP length          the node's standard RLP, exactly the bytes that are hashed and persisted
/// </code>
/// A root key record is <see cref="RootKeyLength"/> bytes long instead.
/// </remarks>
internal static class NodeRecordCodec
{
    internal const int OffsetBits = 32;
    public const int HeaderLength = 48;
    public const int PointerLength = 8;
    public const int RootKeyLength = 88;
    public const int RecordAlignment = 8;

    public const int HashMaskOffset = 2;
    public const int RlpLengthOffset = 4;
    public const int HashOffset = 8;
    public const int NodeKeyHashOffset = 40;

    public const uint TransientGenerationMask = 1u << 31;
    public const ulong TransientPointerMask = (ulong)TransientGenerationMask << OffsetBits;

    public static bool IsTransient(ulong pointer) => (pointer & TransientPointerMask) != 0;

    public static ulong PersistentPointer(ulong pointer) => IsTransient(pointer) ? 0 : pointer;

    /// <summary>Whether <paramref name="node"/> is a record rather than a node's RLP.</summary>
    /// <remarks>A record starts with its <see cref="NodeRecordKind"/>, while node RLP is a list, whose prefix is at least 0xc0.</remarks>
    public static bool IsRecord(ReadOnlySpan<byte> node) => node[0] < 0xc0;

    private const int BranchChildCount = 16;
    private const int MaxKeyNibbles = 64;
    private const int MaxHexPrefixFlag = 3;

    /// <summary>Length of the record that starts with <paramref name="header"/>.</summary>
    public static int Length(ReadOnlySpan<byte> header) => (NodeRecordKind)header[0] == NodeRecordKind.RootKey
        ? RootKeyLength
        : Length(BitOperations.PopCount(BinaryPrimitives.ReadUInt16LittleEndian(header[HashMaskOffset..])),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(header[RlpLengthOffset..]));

    public static int Length(int hashChildCount, int rlpLength) => HeaderLength + PointerLength * hashChildCount + rlpLength;

    public static int AlignedLength(int length) => (length + RecordAlignment - 1) & -RecordAlignment;

    /// <summary>A well-mixed 64-bit hash of a node position, which identifies the records of one trie path.</summary>
    /// <param name="address">The storage trie's address hash, or null for the state trie.</param>
    public static ulong NodeKeyHash(Hash256? address, in TreePath path)
    {
        ulong addressWord = address is null ? 0 : MemoryMarshal.Read<ulong>(address.Bytes);
        ReadOnlySpan<ulong> pathWords = MemoryMarshal.Cast<byte, ulong>(path.Path.Bytes);
        ulong key = addressWord * 0x9E3779B97F4A7C15
            ^ pathWords[0]
            ^ BitOperations.RotateLeft(pathWords[1], 17)
            ^ BitOperations.RotateLeft(pathWords[2], 31)
            ^ BitOperations.RotateLeft(pathWords[3], 47)
            ^ (ulong)path.Length * 0xC2B2AE3D27D4EB4F;

        // The MurmurHash3 64-bit finalizer.
        key ^= key >> 33;
        key *= 0xff51afd7ed558ccd;
        key ^= key >> 33;
        key *= 0xc4ceb9fe1a85ec53;
        return key ^ key >> 33;
    }

    /// <summary>Decodes the record kind and hash mask of a node's RLP for <see cref="WriteFromRlp"/>.</summary>
    /// <returns>False when the RLP is malformed, embeds a child that is not a leaf, or gives a branch a value.</returns>
    public static bool TryDecode(ReadOnlySpan<byte> rlp, out NodeRecordKind kind, out ushort hashMask)
    {
        kind = default;
        hashMask = 0;
        try
        {
            RlpReader reader = new(rlp);
            int length = reader.ReadSequenceLength();
            if (reader.Position + length != rlp.Length) return false;

            int itemCount = reader.PeekNumberOfItemsRemaining(maxSearch: 3);
            if (itemCount > 2) return TryDecodeBranch(ref reader, out kind, out hashMask);
            if (itemCount < 2) return false;

            ReadOnlySpan<byte> hexPrefix = reader.DecodeByteArraySpan();
            if (!IsHexPrefix(hexPrefix)) return false;

            if (NodeRlp.IsLeafHexPrefix(hexPrefix))
            {
                kind = NodeRecordKind.Leaf;
                reader.DecodeByteArraySpan();
                return true;
            }

            kind = NodeRecordKind.Extension;
            if (NodeRlp.HexPrefixNibbleCount(hexPrefix) == 0 || !TryDecodeChild(ref reader, out ChildRefKind child) || child == ChildRefKind.Empty) return false;

            hashMask = (ushort)(child == ChildRefKind.Hash ? 1 : 0);
            return true;
        }
        // RlpReader throws on malformed RLP, and indexes or slices past the end of a truncated item.
        catch (Exception e) when (e is RlpException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>Writes the record of <paramref name="rlp"/>, with every child pointer 0.</summary>
    /// <param name="kind">From <see cref="TryDecode"/>.</param>
    /// <param name="hashMask">From <see cref="TryDecode"/>.</param>
    /// <param name="destination">At least <see cref="Length(int, int)"/> bytes.</param>
    public static void WriteFromRlp(ReadOnlySpan<byte> rlp, NodeRecordKind kind, ushort hashMask, in ValueHash256 hash, ulong nodeKeyHash, Span<byte> destination)
    {
        int pointersLength = PointerLength * BitOperations.PopCount(hashMask);
        destination[..HashOffset].Clear();
        destination[0] = (byte)kind;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[HashMaskOffset..], hashMask);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[RlpLengthOffset..], (uint)rlp.Length);
        hash.Bytes.CopyTo(destination[HashOffset..]);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[NodeKeyHashOffset..], nodeKeyHash);
        destination.Slice(HeaderLength, pointersLength).Clear();
        rlp.CopyTo(destination[(HeaderLength + pointersLength)..]);
    }

    private static bool TryDecodeBranch(ref RlpReader reader, out NodeRecordKind kind, out ushort hashMask)
    {
        kind = NodeRecordKind.HashedBranch;
        hashMask = 0;
        for (int i = 0; i < BranchChildCount; i++)
        {
            if (!TryDecodeChild(ref reader, out ChildRefKind child)) return false;

            if (child == ChildRefKind.Hash) hashMask |= (ushort)(1 << i);
            else if (child == ChildRefKind.Inline) kind = NodeRecordKind.InlineBranch;
        }

        return reader.DecodeByteArraySpan().IsEmpty && reader.Position == reader.Length;
    }

    /// <returns>False for a child that is malformed or embeds anything but a leaf.</returns>
    private static bool TryDecodeChild(ref RlpReader reader, out ChildRefKind kind)
    {
        if (reader.IsSequenceNext())
        {
            kind = ChildRefKind.Inline;
            ReadOnlySpan<byte> inlineRlp = reader.PeekNextItem();
            reader.SkipBytes(inlineRlp.Length);
            return inlineRlp.Length < ValueHash256.MemorySize && IsInlineLeaf(inlineRlp);
        }

        ReadOnlySpan<byte> hash = reader.DecodeByteArraySpan();
        kind = hash.IsEmpty ? ChildRefKind.Empty : ChildRefKind.Hash;
        return hash.IsEmpty || hash.Length == ValueHash256.MemorySize;
    }

    /// <remarks>An embedded branch or extension would need two keys sharing 224 bits, which keccak keys never do.</remarks>
    internal static bool IsInlineLeaf(ReadOnlySpan<byte> rlp)
    {
        RlpReader reader = new(rlp);
        reader.ReadSequenceLength();
        ReadOnlySpan<byte> key = reader.DecodeByteArraySpan();
        reader.DecodeByteArraySpan();
        return reader.Position == reader.Length && IsHexPrefix(key) && NodeRlp.IsLeafHexPrefix(key);
    }

    private static bool IsHexPrefix(ReadOnlySpan<byte> key) =>
        !key.IsEmpty
        && key[0] >> 4 <= MaxHexPrefixFlag
        && NodeRlp.HexPrefixNibbleCount(key) <= MaxKeyNibbles;
}
