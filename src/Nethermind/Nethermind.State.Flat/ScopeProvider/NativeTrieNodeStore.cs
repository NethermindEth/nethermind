// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Native;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// The <see cref="INativeNodeStore"/> of one trie in a <see cref="SnapshotBundle"/>: the state trie when
/// <paramref name="addressHash"/> is null, otherwise that account's storage trie.
/// </summary>
/// <remarks>
/// Written nodes stay pending, and are read back from here, until <see cref="Publish"/> moves them into the bundle,
/// so like the managed tries nothing reaches the bundle before the scope commits. The native bulk set reads and writes
/// from several threads at once, which the bundle's node dictionaries do not allow while they are written.
/// </remarks>
internal sealed class NativeTrieNodeStore(SnapshotBundle bundle, Hash256? addressHash) : INativeNodeStore
{
    private readonly ConcurrentDictionary<TreePath, (ValueHash256 Hash, byte[] Rlp)> _pending = new();
    private volatile bool _hasPending;

    /// <summary>Whether nodes were written since the last <see cref="Publish"/> or <see cref="Clear"/>.</summary>
    public bool HasPending => _hasPending;

    /// <summary>Applies <paramref name="entries"/> to the trie rooted at <paramref name="root"/> and returns the new root.</summary>
    public Hash256 BulkSet(Hash256 root, ReadOnlySpan<PatriciaTree.BulkSetEntry> entries) =>
        NativePatriciaBulkSet.BulkSet(this, root.ValueHash256, entries);

    public int Fetch(in TreePath path, in ValueHash256 hash, Span<byte> buffer)
    {
        if (_pending.TryGetValue(path, out (ValueHash256 Hash, byte[] Rlp) pending) && pending.Hash == hash)
            return Copy(pending.Rlp, buffer);

        Hash256 nodeHash = new(hash);
        TrieNode node = addressHash is null
            ? bundle.FindStateNodeOrUnknown(path, nodeHash)
            : bundle.FindStorageNodeOrUnknown(addressHash, path, nodeHash);
        if (node.Keccak != nodeHash)
            throw new NodeHashMismatchException($"Node hash mismatch. Address {addressHash}. Path: {path}. Hash: {node.Keccak} vs Requested: {nodeHash}");

        if (!node.FullRlp.IsNull) return Copy(node.FullRlp.AsSpan(), buffer);

        byte[]? rlp = addressHash is null
            ? bundle.TryLoadStateRlp(path, nodeHash, ReadFlags.None)
            : bundle.TryLoadStorageRlp(addressHash, path, nodeHash, ReadFlags.None);
        return Copy(rlp, buffer);
    }

    public void Write(in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> rlp)
    {
        _pending[path] = (hash, rlp.ToArray());
        _hasPending = true;
    }

    /// <summary>Moves the pending nodes into the bundle, as the managed committer would.</summary>
    public void Publish()
    {
        if (!_hasPending) return;

        List<(TreePath Path, TrieNode Node)> nodes = new(_pending.Count);
        foreach (KeyValuePair<TreePath, (ValueHash256 Hash, byte[] Rlp)> entry in _pending)
        {
            // Built from its RLP with no children loaded, which is how persisted nodes come back too.
            nodes.Add((entry.Key, new TrieNode(NodeType.Unknown, new Hash256(entry.Value.Hash), new CappedArray<byte>(entry.Value.Rlp))));
        }

        if (addressHash is null) bundle.PublishStateNodes([nodes]);
        else bundle.PublishStorageNodes(bundle.GetStorageNodeDestination(addressHash), addressHash, [nodes]);
        Clear();
    }

    /// <summary>Drops the pending nodes, as when the storage is cleared.</summary>
    public void Clear()
    {
        _pending.Clear();
        _hasPending = false;
    }

    private static int Copy(ReadOnlySpan<byte> rlp, Span<byte> buffer)
    {
        if (rlp.Length <= buffer.Length) rlp.CopyTo(buffer);
        return rlp.Length;
    }
}
