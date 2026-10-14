// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
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
/// Written nodes stay pending in <see cref="NativePendingNodes"/>, where the native bulk set reads them back, until
/// <see cref="Publish"/> moves them into the bundle, so like the managed tries nothing reaches the bundle before the scope
/// commits. The native bulk set writes from several threads at once, which the bundle's node dictionaries do not allow.
/// </remarks>
internal sealed class NativeTrieNodeStore(SnapshotBundle bundle, Hash256? addressHash) : INativeNodeStore
{
    private const int MinNodesToPublishInParallel = 256;

    private NativePendingNodes? _pending;

    /// <summary>Whether nodes were written since the last <see cref="Publish"/> or <see cref="Clear"/>.</summary>
    public bool HasPending => _pending is { Count: > 0 };

    /// <summary>Applies <paramref name="entries"/> to the trie rooted at <paramref name="root"/> and returns the new root.</summary>
    public Hash256 BulkSet(Hash256 root, ReadOnlySpan<PatriciaTree.BulkSetEntry> entries) =>
        NativePatriciaBulkSet.BulkSet(this, _pending ??= NativePendingNodes.Rent(entries.Length), root.ValueHash256, entries);

    public int Fetch(in TreePath path, in ValueHash256 hash, Span<byte> buffer)
    {
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

    /// <summary>Moves the pending nodes into the bundle, as the managed committer would.</summary>
    public void Publish()
    {
        if (_pending is null) return;

        NativePendingNodesView view = _pending.GetView();
        if (view.Count > 0)
        {
            List<(TreePath Path, TrieNode Node)> nodes = new(view.Count);
            CollectionsMarshal.SetCount(nodes, view.Count);
            if (view.Count < MinNodesToPublishInParallel)
            {
                Span<(TreePath Path, TrieNode Node)> destination = CollectionsMarshal.AsSpan(nodes);
                for (int i = 0; i < destination.Length; i++) destination[i] = CreateNode(view, i);
            }
            else
            {
                Parallel.For(0, view.Count, i => CollectionsMarshal.AsSpan(nodes)[i] = CreateNode(view, i));
            }

            if (addressHash is null) bundle.PublishStateNodes([nodes]);
            else bundle.PublishStorageNodes(bundle.GetStorageNodeDestination(addressHash), addressHash, [nodes]);
        }

        Clear();
    }

    /// <summary>Drops the pending nodes, as when the storage is cleared.</summary>
    public void Clear()
    {
        _pending?.Dispose();
        _pending = null;
    }

    // Built from its RLP with no children loaded, which is how persisted nodes come back too.
    private static (TreePath, TrieNode) CreateNode(NativePendingNodesView view, int index)
    {
        ref readonly NativeWrittenNode written = ref view[index];
        return (written.Path, new TrieNode(NodeType.Unknown, new Hash256(written.Hash), new CappedArray<byte>(view.GetRlp(written).ToArray())));
    }

    private static int Copy(ReadOnlySpan<byte> rlp, Span<byte> buffer)
    {
        if (rlp.Length <= buffer.Length) rlp.CopyTo(buffer);
        return rlp.Length;
    }
}
