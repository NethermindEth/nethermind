// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.NodeLog;

internal enum NodeRecordKind : byte
{
    Leaf,
    Extension,

    /// <summary>A branch whose every non-empty child is referenced by hash.</summary>
    HashedBranch,

    /// <summary>A branch with at least one embedded child.</summary>
    InlineBranch,

    /// <summary>A log-only record indexing a trie root; never a node.</summary>
    RootKey,
}

/// <summary>The byte layout of a trie node record, shared by the trie node log and warmed nodes.</summary>
/// <remarks>
/// All fields are little-endian:
/// <code>
/// 0   1   kind (<see cref="NodeRecordKind"/>)
/// 1   1   0
/// 2   2   hash mask: bit i set when child i is referenced by hash (an extension's child is bit 0)
/// 4   4   RLP length
/// 8   32  keccak256 of the RLP
/// 40  8   node key, <see cref="NodeKey"/>
/// 48  8 * popcount(mask)  log pointers of the hash children, ascending child index, 0 when unknown
/// ..  RLP length          the node's standard RLP, exactly the bytes that are hashed and persisted
/// </code>
/// A root key record is <see cref="RootKeyLength"/> bytes long instead.
/// </remarks>
internal static class NodeRecord
{
    public const int HeaderLength = 48;
    public const int PointerLength = 8;
    public const int RootKeyLength = 88;
    public const int RecordAlignment = 8;

    public const int HashMaskOffset = 2;
    public const int RlpLengthOffset = 4;
    public const int HashOffset = 8;
    public const int NodeKeyOffset = 40;

    public const uint TransientGenerationMask = 1u << 31;
    public const ulong TransientPointerMask = (ulong)TransientGenerationMask << TrieNodeLog.OffsetBits;

    public static bool IsTransient(ulong pointer) => (pointer & TransientPointerMask) != 0;

    public static ulong PersistentPointer(ulong pointer) => IsTransient(pointer) ? 0 : pointer;

    internal static uint NextTransientGeneration(ref long epochCounter)
    {
        long epoch = Interlocked.Increment(ref epochCounter);
        if ((ulong)epoch >= TransientGenerationMask) throw new TrieException("Transient trie node generation IDs exhausted.");
        return TransientGenerationMask | (uint)epoch;
    }

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

    /// <summary>A well-mixed 64-bit key of a node position, which identifies the records of one trie path.</summary>
    /// <param name="address">The storage trie's address hash, or null for the state trie.</param>
    public static ulong NodeKey(Hash256? address, in TreePath path)
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

    /// <summary>Classifies a node's RLP for <see cref="WriteFromRlp"/>.</summary>
    /// <returns>False when the RLP is malformed, embeds a child that is not a leaf, or gives a branch a value.</returns>
    public static bool TryMeasure(ReadOnlySpan<byte> rlp, out NodeRecordKind kind, out ushort hashMask)
    {
        kind = default;
        hashMask = 0;
        if (!NodeRlp.NextItem(rlp, out NodeRlp.Item node, out ReadOnlySpan<byte> rest) || !node.IsList || !rest.IsEmpty) return false;
        if (!NodeRlp.NextItem(node.Payload, out NodeRlp.Item first, out rest) || !NodeRlp.NextItem(rest, out NodeRlp.Item second, out rest)) return false;
        if (!rest.IsEmpty) return TryMeasureBranch(node.Payload, out kind, out hashMask);
        if (!IsHexPrefix(first)) return false;

        int keyLength = NodeRlp.HexPrefixNibbleCount(first.Payload);
        if (NodeRlp.IsLeafHexPrefix(first.Payload))
        {
            kind = NodeRecordKind.Leaf;
            return !second.IsList;
        }

        kind = NodeRecordKind.Extension;
        if (keyLength == 0 || !TryMeasureChild(second, out ChildRefKind child) || child == ChildRefKind.Empty) return false;

        hashMask = (ushort)(child == ChildRefKind.Hash ? 1 : 0);
        return true;
    }

    /// <summary>Writes the record of <paramref name="rlp"/>, with every child pointer 0.</summary>
    /// <param name="kind">From <see cref="TryMeasure"/>.</param>
    /// <param name="hashMask">From <see cref="TryMeasure"/>.</param>
    /// <param name="destination">At least <see cref="Length(int, int)"/> bytes.</param>
    public static void WriteFromRlp(ReadOnlySpan<byte> rlp, NodeRecordKind kind, ushort hashMask, in ValueHash256 hash, ulong nodeKey, Span<byte> destination)
    {
        int pointersLength = PointerLength * BitOperations.PopCount(hashMask);
        destination[..HashOffset].Clear();
        destination[0] = (byte)kind;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[HashMaskOffset..], hashMask);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[RlpLengthOffset..], (uint)rlp.Length);
        hash.Bytes.CopyTo(destination[HashOffset..]);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[NodeKeyOffset..], nodeKey);
        destination.Slice(HeaderLength, pointersLength).Clear();
        rlp.CopyTo(destination[(HeaderLength + pointersLength)..]);
    }

    private static bool TryMeasureBranch(ReadOnlySpan<byte> payload, out NodeRecordKind kind, out ushort hashMask)
    {
        kind = NodeRecordKind.HashedBranch;
        hashMask = 0;
        for (int i = 0; i < BranchChildCount; i++)
        {
            if (!NodeRlp.NextItem(payload, out NodeRlp.Item item, out payload) || !TryMeasureChild(item, out ChildRefKind child)) return false;

            if (child == ChildRefKind.Hash) hashMask |= (ushort)(1 << i);
            else if (child == ChildRefKind.Inline) kind = NodeRecordKind.InlineBranch;
        }

        return NodeRlp.NextItem(payload, out NodeRlp.Item value, out payload)
            && value is { IsList: false, Payload.IsEmpty: true }
            && payload.IsEmpty;
    }

    /// <returns>False for a child that is malformed or embeds anything but a leaf.</returns>
    private static bool TryMeasureChild(in NodeRlp.Item item, out ChildRefKind kind)
    {
        kind = item switch
        {
            { IsList: true } => ChildRefKind.Inline,
            { Payload.Length: 0 } => ChildRefKind.Empty,
            _ => ChildRefKind.Hash,
        };

        return kind switch
        {
            ChildRefKind.Inline => item.Encoded.Length < ValueHash256.MemorySize && IsInlineLeaf(item.Encoded),
            ChildRefKind.Hash => item.Payload.Length == ValueHash256.MemorySize,
            _ => true,
        };
    }

    /// <remarks>An embedded branch or extension would need two keys sharing 224 bits, which keccak keys never do.</remarks>
    internal static bool IsInlineLeaf(ReadOnlySpan<byte> rlp) =>
        NodeRlp.NextItem(rlp, out NodeRlp.Item node, out _)
        && NodeRlp.NextItem(node.Payload, out NodeRlp.Item key, out ReadOnlySpan<byte> rest)
        && NodeRlp.NextItem(rest, out NodeRlp.Item value, out rest)
        && rest.IsEmpty
        && IsHexPrefix(key)
        && NodeRlp.IsLeafHexPrefix(key.Payload)
        && !value.IsList;

    private static bool IsHexPrefix(in NodeRlp.Item key) =>
        key is { IsList: false, Payload.IsEmpty: false }
        && key.Payload[0] >> 4 <= MaxHexPrefixFlag
        && NodeRlp.HexPrefixNibbleCount(key.Payload) <= MaxKeyNibbles;
}
