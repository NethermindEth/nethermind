// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core.Crypto;
using Nethermind.Core;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Trie.NodeBuffer;

/// <summary>Reads a node record in place; see <see cref="NodeRecordCodec"/> for the layout.</summary>
/// <remarks>The record's RLP is trusted to be well formed, as <see cref="NodeRecordCodec.TryDecode"/> or the encoder left it.</remarks>
public readonly ref struct NodeRecordRef
{
    private readonly ReadOnlySpan<byte> _record;

    internal NodeRecordRef(ReadOnlySpan<byte> record) => _record = record;

    internal NodeRecordKind Kind => (NodeRecordKind)_record[0];

    internal ushort HashMask => BinaryPrimitives.ReadUInt16LittleEndian(_record[NodeRecordCodec.HashMaskOffset..]);

    /// <summary>Keccak of the node's RLP; zero for a node embedded in its parent, which has no hash of its own.</summary>
    public ref readonly ValueHash256 Hash => ref MemoryMarshal.AsRef<ValueHash256>(_record.Slice(NodeRecordCodec.HashOffset, ValueHash256.MemorySize));

    /// <summary>The node's standard RLP, exactly the bytes that are hashed and persisted.</summary>
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
    internal ChildRef Child(int index)
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
    internal void CopyChildren(Span<ChildRef> children)
    {
        Debug.Assert(Kind is NodeRecordKind.HashedBranch or NodeRecordKind.InlineBranch);
        int position = 0;
        if (Kind == NodeRecordKind.HashedBranch)
        {
            CopyHashedBranchChildren(children);
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

    private void CopyHashedBranchChildren(Span<ChildRef> children)
    {
        Debug.Assert(children.Length == TrieNode.BranchesCount);
        ref byte recordRef = ref MemoryMarshal.GetReference(_record);
        uint hashMask = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref recordRef, NodeRecordCodec.HashMaskOffset));
        uint rlpLength = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref recordRef, NodeRecordCodec.RlpLengthOffset));
        ref byte pointer = ref Unsafe.Add(ref recordRef, NodeRecordCodec.HeaderLength);
        int sequenceHeader = 1 + (rlpLength > 56 ? 1 : 0) + (rlpLength > 257 ? 1 : 0);
        ref byte item = ref Unsafe.Add(ref pointer, NodeRecordCodec.PointerLength * BitOperations.PopCount(hashMask) + sequenceHeader);
        ref ChildRef child = ref MemoryMarshal.GetReference(children);

        if (hashMask == 0xFFFF)
        {
            for (int i = 0; i < TrieNode.BranchesCount; i++)
                WriteHashChild(ref Unsafe.Add(ref child, i), ref Unsafe.Add(ref pointer, i * NodeRecordCodec.PointerLength), ref Unsafe.Add(ref item, i * Serialization.Rlp.Rlp.LengthOfKeccakRlp + 1));
            return;
        }

        for (int i = 0; i < TrieNode.BranchesCount; i++)
        {
            if ((hashMask & (1u << i)) == 0)
            {
                child = default;
                item = ref Unsafe.Add(ref item, 1);
            }
            else
            {
                WriteHashChild(ref child, ref pointer, ref Unsafe.Add(ref item, 1));
                pointer = ref Unsafe.Add(ref pointer, NodeRecordCodec.PointerLength);
                item = ref Unsafe.Add(ref item, Serialization.Rlp.Rlp.LengthOfKeccakRlp);
            }

            child = ref Unsafe.Add(ref child, 1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteHashChild(ref ChildRef child, ref byte pointer, ref byte hash)
    {
        Unsafe.As<ChildRef, Vector128<ulong>>(ref child) = Vector128.Create((ulong)ChildRefKind.Hash, Unsafe.ReadUnaligned<ulong>(ref pointer));
        Unsafe.As<ValueHash256, Vector256<byte>>(ref child.Data) = Unsafe.ReadUnaligned<Vector256<byte>>(ref hash);
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

    /// <summary>Gets the hash of child <paramref name="index"/> of a branch, or of an extension's child at index 0.</summary>
    /// <returns>False when there is no such child, or it is embedded rather than referenced by hash.</returns>
    public bool TryGetChildHash(int index, out ValueHash256 hash)
    {
        ChildRef child = Kind switch
        {
            NodeRecordKind.HashedBranch or NodeRecordKind.InlineBranch when (uint)index < TrieNode.BranchesCount => Child(index),
            NodeRecordKind.Extension when index == 0 => Child(0),
            _ => default,
        };
        bool isHash = child.Kind == ChildRefKind.Hash;
        hash = isHash ? child.Data : default;
        return isHash;
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
