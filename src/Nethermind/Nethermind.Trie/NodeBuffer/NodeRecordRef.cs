// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core;
using Nethermind.Serialization.FluentRlp;

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
            reader.SkipItem<OffFlag>();
            return reader.ReadBytes<OffFlag>();
        }
    }

    private ReadOnlySpan<byte> Payload
    {
        get
        {
            ReadOnlySpan<byte> rlp = Rlp;
            int position = 0;
            new NodeRlpReader(rlp).ReadSequenceLength(ref position, out int length);
            return rlp.Slice(position, length);
        }
    }

    private ReadOnlySpan<byte> KeyHexPrefix => new RlpReader(Payload).ReadBytes<OffFlag>();

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
                    NodeRlpReader reader = new(Payload);
                    int offset = 0;
                    reader.SkipItems(ref offset, index);
                    return ReadChild(reader, ref offset, position);
                }
            case NodeRecordKind.Extension:
                {
                    NodeRlpReader reader = new(Payload);
                    int offset = 0;
                    reader.SkipItem(ref offset);
                    return ReadChild(reader, ref offset, 0);
                }
            default:
                return ThrowNoChildren();
        }
    }

    /// <summary>Writes every child of a branch into <paramref name="children"/>, as <see cref="Child"/> would one at a time, in one pass over the RLP.</summary>
    public void CopyChildren(Span<ChildRef> children)
    {
        Debug.Assert(Kind is NodeRecordKind.HashedBranch or NodeRecordKind.InlineBranch);
        int position = 0;
        if (Kind == NodeRecordKind.HashedBranch)
        {
            // Every child is either 0x80 or a 33-byte hash, so its offset needs no parsing.
            ReadOnlySpan<byte> payload = Payload;
            ushort hashMask = HashMask;
            int offset = 0;
            for (int i = 0; i < children.Length; i++)
            {
                if ((hashMask & (1 << i)) == 0)
                {
                    children[i] = default;
                    offset++;
                }
                else
                {
                    children[i] = ChildRef.ForHash(new ValueHash256(payload.Slice(offset + 1, ValueHash256.MemorySize)), Pointer(position++));
                    offset += ValueHash256.MemorySize + 1;
                }
            }

            return;
        }

        NodeRlpReader reader = new(Payload);
        int cursor = 0;
        for (int i = 0; i < children.Length; i++)
        {
            children[i] = ReadChild(reader, ref cursor, position);
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

    private ChildRef ReadChild(NodeRlpReader reader, ref int offset, int position)
    {
        // Most children are empty or a hash, which the prefix byte alone tells.
        ReadOnlySpan<byte> data = reader.Data;
        if ((uint)offset < (uint)data.Length)
        {
            byte prefix = data[offset];
            if (prefix == Serialization.Rlp.Rlp.EmptyByteArrayByte)
            {
                offset++;
                return default;
            }

            if (prefix == NodeRlpReader.KeccakRlpPrefix && data.Length - offset > ValueHash256.MemorySize)
            {
                ChildRef hashed = ChildRef.ForHash(new ValueHash256(data.Slice(offset + 1, ValueHash256.MemorySize)), Pointer(position));
                offset += 1 + ValueHash256.MemorySize;
                return hashed;
            }
        }

        if (reader.IsSequenceNext(offset))
        {
            int length = reader.PeekNextRlpLength(offset);
            ChildRef inline = ChildRef.ForInline(reader.Data.Slice(offset, length));
            offset += length;
            return inline;
        }

        reader.DecodeByteArraySpan(ref offset, out ReadOnlySpan<byte> hash);
        return hash.IsEmpty ? default : ChildRef.ForHash(new ValueHash256(hash), Pointer(position));
    }

    [DoesNotReturn, StackTraceHidden]
    private ChildRef ThrowNoChildren() => throw new TrieException($"A {Kind} record has no children.");
}
