// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.NodeBuffer;

/// <summary>The records of the nodes a traversal loads from an <see cref="INodeRecordStore"/>, readable until <see cref="Dispose"/>.</summary>
/// <remarks>Records are written to pooled chunks that never move, so a record handed out stays valid as more are added.</remarks>
internal sealed class LoadedRecordBuffer : IDisposable
{
    private const int ChunkBytes = 64 * 1024;

    private static readonly ReadOnlySpanAction<byte, Target> Write = static (node, target) => target.Record = target.Buffer.Add(node, target.Hash);

    private readonly List<byte[]> _chunks = [];
    private int _used = ChunkBytes;

    /// <summary>Loads the node of <paramref name="hash"/> at <paramref name="path"/> from <paramref name="store"/> into this buffer.</summary>
    /// <returns>False when the store has no such node.</returns>
    /// <exception cref="TrieException">The store returns RLP that is not a valid trie node.</exception>
    public bool TryLoad(INodeRecordStore store, in TreePath path, in ValueHash256 hash, out NodeRecord record)
    {
        record = default;
        return store.TryLoad(path, hash, Write, new Target(this, in hash, ref record));
    }

    /// <summary>Releases every record, which must no longer be read.</summary>
    public void Clear()
    {
        foreach (byte[] chunk in _chunks) ArrayPool<byte>.Shared.Return(chunk);
        _chunks.Clear();
        _used = ChunkBytes;
    }

    public void Dispose() => Clear();

    /// <summary>Copies a record, or writes the record of RLP as the node of <paramref name="hash"/>, with every child pointer 0.</summary>
    private NodeRecord Add(ReadOnlySpan<byte> node, in ValueHash256 hash)
    {
        if (NodeRecordCodec.IsRecord(node))
        {
            node.CopyTo(Reserve(node.Length, out NodeRecord copied));
            return copied;
        }

        if (!NodeRecordCodec.TryDecode(node, out NodeRecordKind kind, out ushort hashMask)) ThrowInvalidRlp();

        int length = NodeRecordCodec.Length(BitOperations.PopCount(hashMask), node.Length);
        NodeRecordCodec.WriteFromRlp(node, kind, hashMask, hash, 0, Reserve(length, out NodeRecord record));
        return record;
    }

    private Span<byte> Reserve(int length, out NodeRecord record)
    {
        if (length > ChunkBytes - _used)
        {
            _chunks.Add(ArrayPool<byte>.Shared.Rent(Math.Max(ChunkBytes, length)));
            _used = 0;
        }

        byte[] chunk = _chunks[^1];
        record = new NodeRecord(chunk, _used);
        Span<byte> destination = chunk.AsSpan(_used, length);
        _used += length;
        return destination;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowInvalidRlp() => throw new TrieException("Invalid trie node RLP.");

    private readonly ref struct Target
    {
        public readonly LoadedRecordBuffer Buffer;
        public readonly ref readonly ValueHash256 Hash;
        public readonly ref NodeRecord Record;

        public Target(LoadedRecordBuffer buffer, ref readonly ValueHash256 hash, ref NodeRecord record)
        {
            Buffer = buffer;
            Hash = ref hash;
            Record = ref record;
        }
    }
}
