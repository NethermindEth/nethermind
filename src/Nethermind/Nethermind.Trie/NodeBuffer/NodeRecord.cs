// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.NodeBuffer;

/// <summary>A trie node record held in place inside a buffer of the trie node buffer or warmed nodes.</summary>
/// <remarks>Buffer records borrow native storage for the lifetime of the pin protecting them.</remarks>
public readonly struct NodeRecord
{
    private readonly ReadOnlyMemory<byte> _buffer;
    private readonly int _offset;

    internal NodeRecord(ReadOnlyMemory<byte> buffer, int offset)
    {
        _buffer = buffer;
        _offset = offset;
    }

    /// <summary>The whole record: header, child pointers and RLP.</summary>
    public ReadOnlySpan<byte> Span
    {
        get
        {
            ReadOnlySpan<byte> record = _buffer.Span[_offset..];
            return record[..NodeRecordCodec.Length(record)];
        }
    }

    internal NodeRecordKind Kind => (NodeRecordKind)_buffer.Span[_offset];

    /// <summary>Keccak of the node's RLP.</summary>
    public ref readonly ValueHash256 Hash => ref MemoryMarshal.AsRef<ValueHash256>(_buffer.Span.Slice(_offset + NodeRecordCodec.HashOffset, ValueHash256.MemorySize));

    /// <summary>The node's standard RLP, exactly the bytes that are hashed and persisted.</summary>
    public ReadOnlySpan<byte> Rlp => View.Rlp;

    internal NodeRecordRef View => new(Span);

    /// <summary>A leaf record's value, in place.</summary>
    public ReadOnlyMemory<byte> LeafValue
    {
        get
        {
            ReadOnlySpan<byte> record = Span;
            ReadOnlySpan<byte> value = new NodeRecordRef(record).LeafValue;
            int valueOffset = (int)Unsafe.ByteOffset(ref MemoryMarshal.GetReference(record), ref MemoryMarshal.GetReference(value));
            return _buffer.Slice(_offset + valueOffset, value.Length);
        }
    }

    /// <summary>Number of key nibbles of a leaf or extension record; 0 for a branch.</summary>
    public int KeyLength => View.KeyLength(0);

    /// <summary>Copies the key nibbles of a leaf or extension record, one nibble per byte.</summary>
    /// <param name="nibbles">At least <see cref="KeyLength"/> bytes.</param>
    public void CopyKey(Span<byte> nibbles) => View.CopyKey(0, nibbles);

    /// <summary>Gets the hash of child <paramref name="index"/> of a branch record, or of an extension record's child at index 0.</summary>
    /// <returns>False when there is no such child, or it is embedded rather than referenced by hash.</returns>
    public bool TryGetChildHash(int index, out ValueHash256 hash)
    {
        ChildRef child = Kind switch
        {
            NodeRecordKind.HashedBranch or NodeRecordKind.InlineBranch => View.Child(index),
            NodeRecordKind.Extension when index == 0 => View.Child(0),
            _ => default,
        };
        bool isHash = child.Kind == ChildRefKind.Hash;
        hash = isHash ? child.Data : default;
        return isHash;
    }

    /// <summary>Creates an unresolved <see cref="TrieNode"/> over a copy of the record's RLP, for consumers that still need one.</summary>
    public TrieNode ToTrieNode() => new(NodeType.Unknown, new Hash256(Hash), new CappedArray<byte>(Rlp.ToArray()));
}
