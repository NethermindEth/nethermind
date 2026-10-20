// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// One trie of a <see cref="SnapshotBundle"/>, written by <see cref="NodeRecordBulkSet"/>: the state trie when
/// <paramref name="addressHash"/> is null, otherwise that account's storage trie.
/// </summary>
/// <remarks>
/// Written nodes go to the bundle's <see cref="SnapshotBundle.TrieNodeBuffer"/> at once, but reach the bundle only on <see cref="Publish"/>, so like the
/// managed tries nothing is visible to the bundle's readers before the scope commits. Until then the bulk sets read them
/// back from the buffer by pointer, which the scope's pin keeps readable.
/// </remarks>
/// <param name="root">The trie's root, as <see cref="NodeRecordBufferExtensions.ResolveRoot"/> returns it.</param>
internal sealed class BufferedTrie(SnapshotBundle bundle, Hash256? addressHash, TrieRoot root) : INodeRecordStore
{
    private Hash256 _rootHash = root.ToHash256();
    private bool _rootDirty;

    public TrieRoot Root { get; private set; } = root;

    public Hash256 RootHash => _rootHash;

    /// <summary>Whether the root changed since the last <see cref="Publish"/>, including a change to empty.</summary>
    public bool HasUnpublishedNodes => _rootDirty;

    /// <inheritdoc cref="NodeRecordBulkSet.BulkSet"/>
    public void BulkSet(in ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries, bool canParallelize) =>
        SetRoot(NodeRecordBulkSet.BulkSet(this, addressHash, Root, entries, canParallelize));

    /// <summary>Adopts the root of a completed private update.</summary>
    internal void Adopt(in TrieRoot root) => SetRoot(root);

    /// <summary>Makes the final root readable from the bundle.</summary>
    public void Publish()
    {
        // The endpoint state root is needed even for a no-op block following a changed snapshot.
        if (addressHash is not null && !_rootDirty) return;

        long start = Stopwatch.GetTimestamp();
        // A published root always points at its record, so a root the buffer no longer holds is copied back in.
        if (Root.Pointer == 0 && Root.Hash != ValueKeccak.EmptyTreeHash) SetRoot(new TrieRoot(Root.Hash, CopyRootRecord()));
        if (_rootDirty && Root.Pointer != 0) bundle.TrieNodeBuffer!.SetRoot(addressHash, Root.Hash, Root.Pointer);
        if (addressHash is null) bundle.PublishBufferedStateRoot(Root);
        else bundle.PublishBufferedStorageRoot(addressHash, Root);

        _rootDirty = false;
        Metrics.TrieNodePublishTime.Observe(Stopwatch.GetTimestamp() - start);
    }

    /// <exception cref="MissingTrieNodeException">Neither the warmed nodes nor persistence hold the root.</exception>
    private ulong CopyRootRecord()
    {
        if (!TryLoad(TreePath.Empty, Root.Hash, out NodeRecord record))
            throw NodeRecordBulkSet.MissingNode(addressHash, TreePath.Empty, Root.Hash);

        return bundle.TrieNodeBuffer!.AppendRecordFromRlp(addressHash, TreePath.Empty, Root.Hash, record.Rlp, []);
    }

    /// <summary>Empties the trie and drops the unpublished nodes, as when the storage is cleared.</summary>
    public void Clear()
    {
        SetRoot(TrieRoot.Empty);
        _rootDirty = true;
    }

    /// <remarks>Only for <see cref="Nethermind.Db.IFlatDbConfig.VerifyWithTrie"/>, which checks flat reads against the trie; normal reads never walk the trie.</remarks>
    /// <returns>The value at <paramref name="key"/>, or null when the trie has none.</returns>
    /// <exception cref="MissingTrieNodeException">A node on the path is in neither the buffer nor the bundle.</exception>
    public byte[]? TryGet(in ValueHash256 key)
    {
        Span<byte> keyNibbles = stackalloc byte[BulkSetArena.MaxKeyNibbles];
        Span<byte> nodeKey = stackalloc byte[BulkSetArena.MaxKeyNibbles];
        Nibbles.BytesToNibbleBytes(key.Bytes, keyNibbles);
        TreePath path = TreePath.Empty;
        ChildRef child = Root.Hash == ValueKeccak.EmptyTreeHash ? default : ChildRef.ForHash(Root.Hash, Root.Pointer);
        while (true)
        {
            int depth = path.Length;
            switch (child.Kind)
            {
                case ChildRefKind.Empty:
                    return null;
                case ChildRefKind.Inline:
                    return TryGetInlineLeaf(child.InlineRlp, keyNibbles[depth..], nodeKey);
            }

            if (!TryLocate(child.Pointer, out NodeRecord record) && !TryLoad(path, child.Data, out record))
            {
                throw NodeRecordBulkSet.MissingNode(addressHash, path, child.Data);
            }

            NodeRecordRef view = record.View;
            switch (view.Kind)
            {
                case NodeRecordKind.Leaf:
                    view.CopyKey(0, nodeKey);
                    return nodeKey[..view.KeyLength(0)].SequenceEqual(keyNibbles[depth..]) ? record.LeafValue.Span.ToArray() : null;
                case NodeRecordKind.Extension:
                    int keyLength = view.KeyLength(0);
                    view.CopyKey(0, nodeKey);
                    if (!keyNibbles[depth..].StartsWith(nodeKey[..keyLength])) return null;

                    path.AppendMut(nodeKey[..keyLength]);
                    child = view.Child(0);
                    break;
                default:
                    path.AppendMut(keyNibbles[depth]);
                    child = view.Child(keyNibbles[depth]);
                    break;
            }
        }
    }

    /// <remarks>A node embedded in its parent is always a leaf here, as the bulk set only embeds leaves.</remarks>
    private static byte[]? TryGetInlineLeaf(ReadOnlySpan<byte> rlp, ReadOnlySpan<byte> keyNibbles, Span<byte> nodeKey)
    {
        NodeRlp.NextItem(rlp, out NodeRlp.Item node, out _);
        NodeRlp.NextItem(node.Payload, out NodeRlp.Item hexPrefix, out ReadOnlySpan<byte> rest);
        NodeRlp.NextItem(rest, out NodeRlp.Item value, out _);
        NodeRlp.ReadHexPrefix(hexPrefix.Payload, 0, nodeKey);
        return nodeKey[..NodeRlp.HexPrefixNibbleCount(hexPrefix.Payload)].SequenceEqual(keyNibbles) ? value.Payload.ToArray() : null;
    }

    public bool TryLocate(ulong pointer, out NodeRecord record)
    {
        if (!bundle.TrieNodeBuffer!.TryLocate(pointer, out record)) return false;

        if (!bundle.IsMainBlockProcessing) return true;
        if (NodeRecordCodec.IsTransient(pointer)) Metrics.IncrementWarmedNodeCacheHits();
        else Metrics.IncrementNodeRecordBufferHits();
        return true;
    }

    public INodeRecordWriteBatch StartWriteBatch(int alignedTotalLength) => bundle.TrieNodeBuffer!.StartWriteBatch(alignedTotalLength);

    public bool TryLoad(in TreePath path, in ValueHash256 hash, out NodeRecord record) =>
        bundle.TryLoadTrieNodeRecord(addressHash, path, hash, out record);

    private void SetRoot(in TrieRoot updated)
    {
        bool hashChanged = updated.Hash != Root.Hash;
        _rootDirty |= hashChanged || updated.Pointer != Root.Pointer;
        if (hashChanged) _rootHash = updated.ToHash256();
        Root = updated;
    }
}
