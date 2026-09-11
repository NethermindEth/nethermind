// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Trie;

/// <summary>
/// Hash addressed node storage for standalone trie instances.
/// </summary>
public sealed class MemoryNodeStorage : INodeStorage
{
    private static readonly byte[] EmptyTreeRlp = [128];
    private readonly ConcurrentDictionary<ValueHash256, byte[]> _nodes = new();

    public byte[]? Get(Hash256? address, in TreePath path, in ValueHash256 keccak, ReadFlags readFlags = ReadFlags.None)
    {
        if (keccak == Keccak.EmptyTreeHash.ValueHash256)
        {
            return EmptyTreeRlp;
        }

        return _nodes.TryGetValue(keccak, out byte[]? data) ? data : null;
    }

    public void Set(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> data, WriteFlags writeFlags = WriteFlags.None)
    {
        if (hash == Keccak.EmptyTreeHash.ValueHash256)
        {
            return;
        }

        if (data.IsNull())
        {
            _nodes.TryRemove(hash, out _);
        }
        else
        {
            _nodes[hash] = data.ToArray();
        }
    }

    public INodeStorage.IWriteBatch StartWriteBatch() => new WriteBatch(this);

    public bool KeyExists(in ValueHash256? address, in TreePath path, in ValueHash256 hash) =>
        hash == Keccak.EmptyTreeHash.ValueHash256 || _nodes.ContainsKey(hash);

    public void Flush(bool onlyWal)
    {
    }

    public void Compact()
    {
    }

    private sealed class WriteBatch(MemoryNodeStorage storage) : INodeStorage.IWriteBatch
    {
        public void Set(Hash256? address, in TreePath path, in ValueHash256 currentNodeKeccak, ReadOnlySpan<byte> data, WriteFlags writeFlags) =>
            storage.Set(address, path, currentNodeKeccak, data, writeFlags);

        public void Dispose()
        {
        }
    }
}
