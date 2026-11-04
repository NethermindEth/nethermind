// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Diagnostics;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// One trie of a <see cref="SnapshotBundle"/>, written by <see cref="NodeRecordBulkSet"/>: the state trie when
/// <paramref name="addressHash"/> is null, otherwise that account's storage trie.
/// </summary>
/// <remarks>
/// Written nodes go to the bundle's <see cref="SnapshotBundle.NodeRecordBuffer"/> at once, but reach the bundle only on <see cref="Publish"/>, so like the
/// managed tries nothing is visible to the bundle's readers before the scope commits. Until then the bulk sets read them
/// back from the buffer by pointer, which the scope's pin keeps readable.
/// </remarks>
/// <param name="root">The trie's root, as <see cref="NodeRecordBufferExtensions.ResolveRoot"/> returns it.</param>
internal sealed class NodeRecordTrie(SnapshotBundle bundle, Hash256? addressHash, (ValueHash256 Hash, ulong Pointer) root) : INodeRecordStore
{
    private Hash256 _rootHash = new(root.Hash);
    private bool _rootDirty;

    public (ValueHash256 Hash, ulong Pointer) Root { get; private set; } = root;

    public Hash256 RootHash => _rootHash;

    /// <summary>Whether the root changed since the last <see cref="Publish"/>, including a change to empty.</summary>
    public bool HasUnpublishedNodes => _rootDirty;

    /// <inheritdoc cref="NodeRecordBulkSet.BulkSet"/>
    public void BulkSet(in ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries, bool canParallelize) =>
        SetRoot(NodeRecordBulkSet.BulkSet(this, addressHash, Root, entries, canParallelize));

    /// <summary>Adopts the root of a completed private update.</summary>
    internal void Adopt(in (ValueHash256 Hash, ulong Pointer) root) => SetRoot(root);

    /// <summary>Makes the final root readable from the bundle.</summary>
    public void Publish()
    {
        // The endpoint state root is needed even for a no-op block following a changed snapshot.
        if (addressHash is not null && !_rootDirty) return;

        long start = Stopwatch.GetTimestamp();
        // A published root always points at its record, so a root the buffer no longer holds is copied back in.
        if (Root.Pointer == 0 && Root.Hash != ValueKeccak.EmptyTreeHash) SetRoot((Root.Hash, CopyRootRecord()));
        if (_rootDirty && Root.Pointer != 0) bundle.NodeRecordBuffer!.SetRoot(addressHash, Root.Hash, Root.Pointer);
        if (addressHash is null) bundle.PublishBufferedStateRoot(Root);
        else bundle.PublishBufferedStorageRoot(addressHash, Root);

        _rootDirty = false;
        Metrics.TrieNodePublishTime.Observe(Stopwatch.GetTimestamp() - start);
    }

    /// <exception cref="MissingTrieNodeException">Neither the warmed nodes nor persistence hold the root.</exception>
    private ulong CopyRootRecord()
    {
        using LoadedRecordBuffer loaded = new();
        if (!loaded.TryLoad(this, TreePath.Empty, Root.Hash, out NodeRecord record))
            throw NodeRecordBulkSet.MissingNode(addressHash, TreePath.Empty, Root.Hash);

        return bundle.NodeRecordBuffer!.AppendRecordFromRlp(addressHash, TreePath.Empty, Root.Hash, record.Rlp, []);
    }

    /// <summary>Empties the trie and drops the unpublished nodes, as when the storage is cleared.</summary>
    public void Clear()
    {
        SetRoot((ValueKeccak.EmptyTreeHash, 0));
        _rootDirty = true;
    }

    public bool TryLocate(ulong pointer, out NodeRecord record) => bundle.TryLocate(pointer, out record);

    public INodeRecordWriteBatch StartWriteBatch(int alignedTotalLength) => bundle.NodeRecordBuffer!.StartWriteBatch(alignedTotalLength);

    public bool TryLoad<TArg>(in TreePath path, in ValueHash256 hash, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct =>
        bundle.TryLoadTrieNodeRecord(addressHash, path, action, arg);

    private void SetRoot(in (ValueHash256 Hash, ulong Pointer) updated)
    {
        bool hashChanged = updated.Hash != Root.Hash;
        _rootDirty |= hashChanged || updated.Pointer != Root.Pointer;
        if (hashChanged) _rootHash = new(updated.Hash);
        Root = updated;
    }

    /// <remarks>Only for <see cref="Nethermind.Db.IFlatDbConfig.VerifyWithTrie"/>, which checks flat reads against the trie; normal reads never walk the trie.</remarks>
    /// <returns>The value at <paramref name="key"/>, or null when the trie has none.</returns>
    /// <exception cref="MissingTrieNodeException">A node on the path is in neither the buffer nor the bundle.</exception>
    public byte[]? TryGet(in ValueHash256 key)
    {
        TreePath keyPath = new(key, BulkSetWorkingState.MaxKeyNibbles);
        Span<byte> nodeKey = stackalloc byte[BulkSetWorkingState.MaxKeyNibbles];
        using LoadedRecordBuffer loaded = new();
        TreePath path = TreePath.Empty;
        ChildRef child = Root.Hash == ValueKeccak.EmptyTreeHash ? default : ChildRef.ForHash(Root.Hash, Root.Pointer);
        while (true)
        {
            switch (child.Kind)
            {
                case ChildRefKind.Empty:
                    return null;
                case ChildRefKind.Inline:
                    // A node embedded in its parent is always a leaf here, as the bulk set only embeds leaves.
                    RlpReader reader = new(child.InlineRlp);
                    reader.ReadSequenceLength();
                    ReadOnlySpan<byte> hexPrefix = reader.DecodeByteArraySpan();
                    ReadOnlySpan<byte> value = reader.DecodeByteArraySpan();
                    int inlineKeyLength = NodeRlp.HexPrefixNibbleCount(hexPrefix);
                    NodeRlp.ReadHexPrefix(hexPrefix, 0, nodeKey);
                    return path.Length + inlineKeyLength == keyPath.Length && path.Append(nodeKey[..inlineKeyLength]) == keyPath ? value.ToArray() : null;
            }

            if (!TryLocate(child.Pointer, out NodeRecord record) && !loaded.TryLoad(this, path, child.Data, out record))
            {
                throw NodeRecordBulkSet.MissingNode(addressHash, path, child.Data);
            }

            NodeRecordRef view = record.View;
            switch (view.Kind)
            {
                case NodeRecordKind.Leaf:
                    int leafKeyLength = view.KeyLength(0);
                    view.CopyKey(0, nodeKey);
                    return path.Length + leafKeyLength == keyPath.Length && path.Append(nodeKey[..leafKeyLength]) == keyPath ? record.LeafValue.Span.ToArray() : null;
                case NodeRecordKind.Extension:
                    int keyLength = view.KeyLength(0);
                    if (keyLength > keyPath.Length - path.Length) return null;

                    view.CopyKey(0, nodeKey);
                    path.AppendMut(nodeKey[..keyLength]);
                    if (!keyPath.StartsWith(path)) return null;

                    child = view.Child(0);
                    break;
                default:
                    int nibble = keyPath[path.Length];
                    path.AppendMut(nibble);
                    child = view.Child(nibble);
                    break;
            }
        }
    }
}
