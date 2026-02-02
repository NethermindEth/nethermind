// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Sync;

/// <summary>
/// ISnapStateTree adapter for flat snap sync.
/// Owns reader (for IsPersisted) and writeBatch (for commits), disposing them on Dispose.
/// </summary>
public class FlatSnapStateTree : ISnapStateTree
{
    private static readonly AccountDecoder _accountDecoder = new();

    private readonly IPersistence.IPersistenceReader _reader;
    private readonly IPersistence.IWriteBatch _writeBatch;
    private readonly StateTree _tree;

    public FlatSnapStateTree(IPersistence.IPersistenceReader reader, IPersistence.IWriteBatch writeBatch, ILogManager logManager)
    {
        _reader = reader;
        _writeBatch = writeBatch;
        _tree = new StateTree(new PersistenceTrieStoreAdapter(reader, writeBatch), logManager);
    }

    public Hash256 RootHash => _tree.RootHash;

    public void SetRootFromProof(TrieNode root) => _tree.RootRef = root;

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak)
    {
        byte[]? rlp = _reader.TryLoadStateRlp(path, ReadFlags.None);
        return rlp is not null && ValueKeccak.Compute(rlp) == keccak;
    }

    public void Clear()
    {
        _writeBatch.Clear();
        _tree.RootHash = Keccak.EmptyTreeHash;
    }

    public void BulkSet(in ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries, PatriciaTree.Flags flags)
    {
        // Persist flat entries directly
        foreach (ref readonly PatriciaTree.BulkSetEntry entry in entries.AsSpan())
        {
            Account account = _accountDecoder.Decode(entry.Value)!;
            _writeBatch.SetAccountRaw(entry.Path.ToCommitment(), account);
        }

        // Build trie as before
        _tree.BulkSet(entries, flags);
    }

    public void UpdateRootHash() => _tree.UpdateRootHash();

    public void Commit(bool skipRoot, WriteFlags writeFlags) => _tree.Commit(skipRoot, writeFlags);

    public void Dispose()
    {
        _writeBatch.Dispose();
        _reader.Dispose();
    }
}
