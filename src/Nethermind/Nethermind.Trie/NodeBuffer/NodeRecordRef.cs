// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.NodeBuffer;

/// <summary>Reads a node record in place; see <see cref="NodeRecordCodec"/> for the layout.</summary>
/// <remarks>The record's RLP is trusted to be well formed, as <see cref="NodeRecordCodec.TryMeasure"/> or the encoder left it.</remarks>
internal readonly ref struct NodeRecordRef(ReadOnlySpan<byte> record)
{
    private readonly ReadOnlySpan<byte> _record = record;

    public NodeRecordKind Kind => (NodeRecordKind)_record[0];

    public ushort HashMask => BinaryPrimitives.ReadUInt16LittleEndian(_record[NodeRecordCodec.HashMaskOffset..]);

    public ref readonly ValueHash256 Hash => ref MemoryMarshal.AsRef<ValueHash256>(_record.Slice(NodeRecordCodec.HashOffset, ValueHash256.MemorySize));

    public ulong NodeKeyHash => BinaryPrimitives.ReadUInt64LittleEndian(_record[NodeRecordCodec.NodeKeyHashOffset..]);

    public ReadOnlySpan<byte> Rlp => _record.Slice(
        NodeRecordCodec.HeaderLength + NodeRecordCodec.PointerLength * BitOperations.PopCount(HashMask),
        (int)BinaryPrimitives.ReadUInt32LittleEndian(_record[NodeRecordCodec.RlpLengthOffset..]));

    /// <summary>The leaf's value, the payload of its second RLP item.</summary>
    public ReadOnlySpan<byte> LeafValue
    {
        get
        {
            NodeRlp.NextItem(Payload, out _, out ReadOnlySpan<byte> rest);
            NodeRlp.NextItem(rest, out NodeRlp.Item value, out _);
            return value.Payload;
        }
    }

    private ReadOnlySpan<byte> Payload
    {
        get
        {
            NodeRlp.NextItem(Rlp, out NodeRlp.Item node, out _);
            return node.Payload;
        }
    }

    private ReadOnlySpan<byte> KeyHexPrefix
    {
        get
        {
            NodeRlp.NextItem(Payload, out NodeRlp.Item key, out _);
            return key.Payload;
        }
    }

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
                    ReadOnlySpan<byte> remaining = Payload;
                    for (int i = 0; i < index; i++)
                    {
                        NodeRlp.NextItem(remaining, out _, out remaining);
                    }

                    NodeRlp.NextItem(remaining, out NodeRlp.Item child, out _);
                    return ReadChild(child, position);
                }
            case NodeRecordKind.Extension:
                {
                    NodeRlp.NextItem(Payload, out _, out ReadOnlySpan<byte> rest);
                    NodeRlp.NextItem(rest, out NodeRlp.Item child, out _);
                    return ReadChild(child, 0);
                }
            default:
                return ThrowNoChildren();
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

    private ChildRef ReadChild(in NodeRlp.Item item, int position)
    {
        if (item.IsList) return ChildRef.ForInline(item.Encoded);
        return item.Payload.IsEmpty ? default : ChildRef.ForHash(new ValueHash256(item.Payload), Pointer(position));
    }

    [DoesNotReturn, StackTraceHidden]
    private ChildRef ThrowNoChildren() => throw new TrieException($"A {Kind} record has no children.");
}
