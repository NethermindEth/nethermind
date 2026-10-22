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

    /// <summary>Whether this references no record.</summary>
    public bool IsNull => _buffer.Equals(default);

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
    internal ReadOnlyMemory<byte> LeafValue
    {
        get
        {
            ReadOnlySpan<byte> record = Span;
            ReadOnlySpan<byte> value = new NodeRecordRef(record).LeafValue;
            int valueOffset = (int)Unsafe.ByteOffset(ref MemoryMarshal.GetReference(record), ref MemoryMarshal.GetReference(value));
            return _buffer.Slice(_offset + valueOffset, value.Length);
        }
    }

    /// <summary>Creates an unresolved <see cref="TrieNode"/> over a copy of the record's RLP, for consumers that still need one.</summary>
    public TrieNode ToTrieNode() => new(NodeType.Unknown, new Hash256(Hash), new CappedArray<byte>(Rlp.ToArray()));
}
