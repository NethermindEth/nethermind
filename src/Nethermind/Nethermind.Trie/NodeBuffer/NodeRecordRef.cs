// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Trie.NodeBuffer;

/// <summary>Reads a node record in place; see <see cref="NodeRecordCodec"/> for the layout.</summary>
/// <remarks>The record's RLP is trusted to be well formed, as <see cref="NodeRecordCodec.TryDecode"/> or the encoder left it.</remarks>
internal readonly ref struct NodeRecordRef(ReadOnlySpan<byte> record)
{
    private readonly ReadOnlySpan<byte> _record = record;

    public NodeRecordKind Kind => (NodeRecordKind)_record[0];

    public ushort HashMask => BinaryPrimitives.ReadUInt16LittleEndian(_record[NodeRecordCodec.HashMaskOffset..]);

    public ReadOnlySpan<byte> Rlp => _record.Slice(
        NodeRecordCodec.HeaderLength + NodeRecordCodec.PointerLength * BitOperations.PopCount(HashMask),
        (int)BinaryPrimitives.ReadUInt32LittleEndian(_record[NodeRecordCodec.RlpLengthOffset..]));

    /// <summary>The leaf's value, the payload of its second RLP item.</summary>
    public ReadOnlySpan<byte> LeafValue
    {
        get
        {
            RlpReader reader = new(Payload);
            reader.SkipItem();
            // Not DecodeByteArraySpan: it hands a one-byte value back from a static table, and NodeRecord.LeafValue needs it in place.
            (int prefixLength, int contentLength) = reader.PeekPrefixAndContentLength();
            return reader.Peek(prefixLength, contentLength);
        }
    }

    private ReadOnlySpan<byte> Payload
    {
        get
        {
            RlpReader reader = new(Rlp);
            int length = reader.ReadSequenceLength();
            return reader.Read(length);
        }
    }

    private ReadOnlySpan<byte> KeyHexPrefix => new RlpReader(Payload).DecodeByteArraySpan();

    /// <summary>Child <paramref name="index"/> of a branch, or the child of an extension, with its buffer pointer.</summary>
    public ChildRef Child(int index)
    {
        ushort hashMask = HashMask;
        int position = BitOperations.PopCount(hashMask & ((1u << index) - 1));
        switch (Kind)
        {
            case NodeRecordKind.HashedBranch:
                // Every child before this one is either 0x80 or a 33-byte hash, so its offset needs no parsing.
                return (hashMask & (1 << index)) == 0
                    ? default
                    : ChildRef.ForHash(new ValueHash256(Payload.Slice(index + ValueHash256.MemorySize * position + 1, ValueHash256.MemorySize)), Pointer(position));
            case NodeRecordKind.InlineBranch:
                {
                    RlpReader reader = new(Payload);
                    reader.SkipItems(index);
                    return ReadChild(ref reader, position);
                }
            case NodeRecordKind.Extension:
                {
                    RlpReader reader = new(Payload);
                    reader.SkipItem();
                    return ReadChild(ref reader, 0);
                }
            default:
                return ThrowNoChildren();
        }
    }

    /// <summary>Writes every child of a branch into <paramref name="children"/>, as <see cref="Child"/> would one at a time, in one pass over the RLP.</summary>
    public void CopyChildren(Span<ChildRef> children)
    {
        Debug.Assert(Kind is NodeRecordKind.HashedBranch or NodeRecordKind.InlineBranch);
        RlpReader reader = new(Payload);
        int position = 0;
        for (int i = 0; i < children.Length; i++)
        {
            children[i] = ReadChild(ref reader, position);
            if (children[i].Kind == ChildRefKind.Hash) position++;
        }
    }

    /// <summary>Number of key nibbles past the first <paramref name="skip"/>; a branch has no key.</summary>
    public int KeyLength(int skip) => Kind is NodeRecordKind.Leaf or NodeRecordKind.Extension
        ? NodeRlp.HexPrefixNibbleCount(KeyHexPrefix) - skip
        : 0;

    /// <summary>Copies the key nibbles past the first <paramref name="skip"/>, one nibble per byte.</summary>
    /// <param name="nibbles">At least <see cref="KeyLength"/> bytes.</param>
    public void CopyKey(int skip, Span<byte> nibbles)
    {
        if (Kind is NodeRecordKind.Leaf or NodeRecordKind.Extension) NodeRlp.ReadHexPrefix(KeyHexPrefix, skip, nibbles);
    }

    private ulong Pointer(int position) =>
        BinaryPrimitives.ReadUInt64LittleEndian(_record[(NodeRecordCodec.HeaderLength + NodeRecordCodec.PointerLength * position)..]);

    private ChildRef ReadChild(ref RlpReader reader, int position)
    {
        if (reader.IsSequenceNext()) return ChildRef.ForInline(reader.Read(reader.PeekNextRlpLength()));
        ReadOnlySpan<byte> hash = reader.DecodeByteArraySpan();
        return hash.IsEmpty ? default : ChildRef.ForHash(new ValueHash256(hash), Pointer(position));
    }

    [DoesNotReturn, StackTraceHidden]
    private ChildRef ThrowNoChildren() => throw new TrieException($"A {Kind} record has no children.");
}
