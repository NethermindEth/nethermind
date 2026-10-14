// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.NodeLog;

/// <summary>A trie node record held in place inside a buffer of the trie node log, warmed nodes or a record cache.</summary>
/// <remarks>Records are immutable once published, so the referenced bytes never change while the buffer is reachable, unless the record table that owns them is reset.</remarks>
public readonly struct NodeRecordRef(byte[] buffer, int offset)
{
    private readonly byte[] _buffer = buffer;
    private readonly int _offset = offset;

    /// <summary>Whether this references no record.</summary>
    public bool IsNull => _buffer is null;

    /// <summary>The whole record: header, child pointers and RLP.</summary>
    public ReadOnlySpan<byte> Span => _buffer.AsSpan(_offset, NodeRecord.Length(_buffer.AsSpan(_offset)));

    internal NodeRecordKind Kind => (NodeRecordKind)_buffer[_offset];

    /// <summary>Keccak of the node's RLP.</summary>
    public ref readonly ValueHash256 Hash => ref MemoryMarshal.AsRef<ValueHash256>(_buffer.AsSpan(_offset + NodeRecord.HashOffset, ValueHash256.MemorySize));

    /// <summary>The node's standard RLP, exactly the bytes that are hashed and persisted.</summary>
    public ReadOnlySpan<byte> Rlp => View.Rlp;

    internal RecordView View => new(Span);

    /// <summary>Creates an unresolved <see cref="TrieNode"/> over a copy of the record's RLP, for consumers that still need one.</summary>
    public TrieNode ToTrieNode() => new(NodeType.Unknown, new Hash256(Hash), new CappedArray<byte>(Rlp.ToArray()));
}
