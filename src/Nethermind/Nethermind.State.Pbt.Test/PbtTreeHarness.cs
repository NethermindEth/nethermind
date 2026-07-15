// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Test;

/// <summary>
/// Drives <see cref="TrieUpdater.UpdateRoot"/> the way the production scope does: raw 32-byte
/// key/value writes are packed into a <see cref="PbtWriteBatch"/> and applied over dictionary-backed
/// node/blob stores that persist across batches.
/// </summary>
public sealed class PbtTreeHarness : IPbtStore
{
    private readonly Dictionary<TrieNodeKey, byte[]> _nodes = [];
    private readonly Dictionary<Stem, byte[]> _blobs = [];
    private ValueHash256 _root;

    public IReadOnlyDictionary<TrieNodeKey, byte[]> Nodes => _nodes;

    public MemoryManager<byte>? GetTrieNode(in TrieNodeKey key) => ArrayMemoryManager.From(_nodes.GetValueOrDefault(key));

    public void SetTrieNode(in TrieNodeKey key, ReadOnlySpan<byte> node)
    {
        if (node.Length == 0) _nodes.Remove(key);
        else _nodes[key] = node.ToArray();
    }

    public MemoryManager<byte>? GetLeafBlob(in Stem stem) => ArrayMemoryManager.From(_blobs.GetValueOrDefault(stem));

    public void SetLeafBlob(in Stem stem, ReadOnlySpan<byte> blob)
    {
        if (blob.Length == 0) _blobs.Remove(stem);
        else _blobs[stem] = blob.ToArray();
    }

    /// <summary>Applies key/value writes (empty/zero value = clear) and returns the new root.</summary>
    public ValueHash256 ApplyBatch(IEnumerable<(byte[] Key, byte[]? Value)> writes)
    {
        using PbtWriteBatch batch = new(estimatedEntries: 64);
        foreach ((byte[] key, byte[]? value) in writes)
        {
            batch.Add(new ValueHash256(key), value ?? default);
        }

        _root = TrieUpdater.UpdateRoot(this, _root, batch);
        return _root;
    }
}
