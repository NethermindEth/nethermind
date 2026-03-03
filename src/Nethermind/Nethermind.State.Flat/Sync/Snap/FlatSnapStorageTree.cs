// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Snap;
using Nethermind.Synchronization;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.Sync.Snap;

/// <summary>
/// ISnapTree adapter for flat snap sync (storage).
/// Owns reader (for IsPersisted) and writeBatch (for commits), disposing them on Dispose.
/// </summary>
public class FlatSnapStorageTree : ISnapTree<PathWithStorageSlot>
{
    private readonly IPersistence.IPersistenceReader _reader;
    private readonly IPersistence.IWriteBatch _writeBatch;
    private readonly StorageTree _tree;
    private readonly Hash256 _addressHash;
    private readonly SnapUpperBoundAdapter _adapter;

    public FlatSnapStorageTree(IPersistence.IPersistenceReader reader, IPersistence.IWriteBatch writeBatch, Hash256 addressHash, ILogManager logManager)
    {
        _reader = reader;
        _writeBatch = writeBatch;
        _addressHash = addressHash;
        _adapter = new SnapUpperBoundAdapter(new PersistenceStorageTrieStoreAdapter(reader, writeBatch, addressHash));
        _tree = new StorageTree(_adapter, logManager);
    }

    public Hash256 RootHash => _tree.RootHash;

    public void SetRootFromProof(TrieNode root) => _tree.RootRef = root;

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak)
    {
        byte[]? rlp = _reader.TryLoadStorageRlp(_addressHash, path, ReadFlags.None);
        return rlp is not null && ValueKeccak.Compute(rlp) == keccak;
    }

    public void BulkSetAndUpdateRootHash(IReadOnlyList<PathWithStorageSlot> entries)
    {
        // Write flat entries directly — no need to decode from trie nodes later
        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> bulkEntries = new(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            PathWithStorageSlot slot = entries[i];
            Rlp.ValueDecoderContext ctx = ((ReadOnlySpan<byte>)slot.SlotRlpValue).AsRlpValueContext();
            _writeBatch.SetStorageRaw(_addressHash, slot.Path.ToCommitment(), SlotValue.FromSpanWithoutLeadingZero(ctx.DecodeByteArraySpan()));

            bulkEntries.Add(new PatriciaTree.BulkSetEntry(slot.Path, slot.SlotRlpValue));
            Interlocked.Add(ref Nethermind.Synchronization.Metrics.SnapStateSynced, slot.SlotRlpValue.Length);
        }

        _tree.BulkSet(bulkEntries, PatriciaTree.Flags.WasSorted);
        _tree.UpdateRootHash();
    }

    public void Commit(ValueHash256 upperBound)
    {
        _adapter.UpperBound = upperBound;
        _tree.Commit(writeFlags: WriteFlags.DisableWAL);
    }

    public void Dispose()
    {
        _writeBatch.Dispose();
        _reader.Dispose();
    }

    /// <summary>
    /// Storage trie store adapter that writes trie nodes to IPersistence.IWriteBatch.
    /// Flat entries are written directly in BulkSetAndUpdateRootHash, so the committer only writes trie nodes.
    /// Uses IPersistenceReader for IsPersisted queries during snap sync.
    /// </summary>
    private class PersistenceStorageTrieStoreAdapter(
        IPersistence.IPersistenceReader reader,
        IPersistence.IWriteBatch writeBatch,
        Hash256 addressHash) : AbstractMinimalTrieStore
    {
        public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => new(NodeType.Unknown, hash);

        public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
            reader.TryLoadStorageRlp(addressHash, path, flags);

        public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
            new StorageCommitter(writeBatch, addressHash);

        private sealed class StorageCommitter(IPersistence.IWriteBatch writeBatch, Hash256 address) : ICommitter
        {
            public TrieNode CommitNode(ref TreePath path, TrieNode node)
            {
                writeBatch.SetStorageTrieNode(address, path, node);
                return node;
            }

            public void Dispose() { }
        }
    }

}
