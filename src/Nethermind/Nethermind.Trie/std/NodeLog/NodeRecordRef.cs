// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.NodeLog;

/// <summary>A trie node record held in place inside a buffer of the trie node log, warmed nodes or a record cache.</summary>
/// <remarks>Log records borrow native storage for the lifetime of their log operation or generation retain. Managed warmed/cache records remain valid until their owning record table is reset.</remarks>
public readonly struct NodeRecordRef
{
    private readonly ReadOnlyMemory<byte> _buffer;
    private readonly int _offset;

    /// <summary>References a record at <paramref name="offset"/> inside a managed buffer.</summary>
    public NodeRecordRef(byte[] buffer, int offset) : this(buffer.AsMemory(), offset) { }

    internal NodeRecordRef(ReadOnlyMemory<byte> buffer, int offset)
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
            return record[..NodeRecord.Length(record)];
        }
    }

    internal NodeRecordKind Kind => (NodeRecordKind)_buffer.Span[_offset];

    /// <summary>Keccak of the node's RLP.</summary>
    public ref readonly ValueHash256 Hash => ref MemoryMarshal.AsRef<ValueHash256>(_buffer.Span.Slice(_offset + NodeRecord.HashOffset, ValueHash256.MemorySize));

    /// <summary>The node's standard RLP, exactly the bytes that are hashed and persisted.</summary>
    public ReadOnlySpan<byte> Rlp => View.Rlp;

    internal RecordView View => new(Span);

    /// <summary>A leaf record's value, in place.</summary>
    internal ValueRef LeafValue
    {
        get
        {
            ReadOnlySpan<byte> record = Span;
            ReadOnlySpan<byte> value = new RecordView(record).LeafValue;
            int valueOffset = (int)Unsafe.ByteOffset(ref MemoryMarshal.GetReference(record), ref MemoryMarshal.GetReference(value));
            return new ValueRef(_buffer, _offset + valueOffset, value.Length);
        }
    }

    /// <summary>Creates an unresolved <see cref="TrieNode"/> over a copy of the record's RLP, for consumers that still need one.</summary>
    public TrieNode ToTrieNode() => new(NodeType.Unknown, new Hash256(Hash), new CappedArray<byte>(Rlp.ToArray()));
}
