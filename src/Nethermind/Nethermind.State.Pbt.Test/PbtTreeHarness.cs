// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Pbt.Test;

/// <summary>
/// Drives <see cref="StemLeafBlob"/> + <see cref="StemTrie"/> the way the production scope does:
/// batches of raw 32-byte key/value writes are grouped by stem, folded into blobs, and pushed
/// through a trie batch over dictionary-backed stores that persist across batches.
/// </summary>
public sealed class PbtTreeHarness : IStemTrieNodeSource
{
    private readonly Dictionary<TrieNodeKey, byte[]> _nodes = [];
    private readonly Dictionary<Stem, byte[]> _blobs = [];

    public IReadOnlyDictionary<TrieNodeKey, byte[]> Nodes => _nodes;

    public byte[]? GetTrieNode(in TrieNodeKey key) => _nodes.GetValueOrDefault(key);

    /// <summary>Applies key/value writes (null or zero value = clear) and returns the new root.</summary>
    public ValueHash256 ApplyBatch(IEnumerable<(byte[] Key, byte[]? Value)> writes)
    {
        Dictionary<Stem, Dictionary<byte, byte[]?>> perStem = [];
        foreach ((byte[] key, byte[]? value) in writes)
        {
            Stem stem = new(key.AsSpan(0, 31));
            if (!perStem.TryGetValue(stem, out Dictionary<byte, byte[]?>? changes))
            {
                perStem[stem] = changes = [];
            }

            changes[key[31]] = value;
        }

        Dictionary<Stem, ValueHash256?> stemChanges = [];
        foreach ((Stem stem, Dictionary<byte, byte[]?> changes) in perStem)
        {
            byte[] blob = StemLeafBlob.Apply(_blobs.GetValueOrDefault(stem, []), changes);
            if (blob.Length == 0)
            {
                _blobs.Remove(stem);
                stemChanges[stem] = null;
            }
            else
            {
                _blobs[stem] = blob;
                stemChanges[stem] = StemLeafBlob.ComputeSubtreeRoot(blob);
            }
        }

        Dictionary<TrieNodeKey, byte[]?> dirtyNodes = [];
        ValueHash256 root = new StemTrie(this).BatchUpdate(stemChanges, dirtyNodes);
        foreach ((TrieNodeKey key, byte[]? node) in dirtyNodes)
        {
            if (node is null)
            {
                _nodes.Remove(key);
            }
            else
            {
                _nodes[key] = node;
            }
        }

        return root;
    }
}
