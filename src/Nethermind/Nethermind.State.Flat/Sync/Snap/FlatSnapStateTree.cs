// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Sync.Snap;

/// <summary>
/// ISnapTree adapter for flat snap sync (state).
/// Owns reader (for IsPersisted) and writeBatch (for commits), disposing them on Dispose.
/// </summary>
public class FlatSnapStateTree : ISnapTree<PathWithAccount>
{
    private readonly IPersistence.IPersistenceReader _reader;
    private readonly IPersistence.IWriteBatch _writeBatch;
    private readonly bool _enableDoubleWriteCheck;
    private readonly SnapUpperBoundAdapter _adapter;
    private readonly StateTree _tree;
    private IReadOnlyList<PathWithAccount>? _pendingEntries;

    public FlatSnapStateTree(IPersistence.IPersistenceReader reader, IPersistence.IWriteBatch writeBatch, bool enableDoubleWriteCheck, ILogManager logManager)
    {
        _reader = reader;
        _writeBatch = writeBatch;
        _enableDoubleWriteCheck = enableDoubleWriteCheck;
        _adapter = new SnapUpperBoundAdapter(new PersistenceTrieStoreAdapter(reader, writeBatch, enableDoubleWriteCheck));
        _tree = new StateTree(_adapter, logManager);
    }

    public Hash256 RootHash => _tree.RootHash;

    public void SetRootFromProof(TrieNode root) => _tree.RootRef = root;

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak)
    {
        byte[]? rlp = _reader.TryLoadStateRlp(path, ReadFlags.None);
        return rlp is not null && ValueKeccak.Compute(rlp) == keccak;
    }

    public void BulkSetAndUpdateRootHash(IReadOnlyList<PathWithAccount> entries)
    {
        _pendingEntries = entries;
        ISnapTree<PathWithAccount>.DoBulkSetAndUpdateRootHash(_tree, entries);
    }

    public void Commit(ValueHash256 upperBound)
    {
        _adapter.UpperBound = upperBound;

        if (_pendingEntries is not null)
        {
            for (int i = 0; i < _pendingEntries.Count; i++)
            {
                PathWithAccount account = _pendingEntries[i];
                if (account.Path <= upperBound)
                {
                    if (_enableDoubleWriteCheck && _reader.GetAccountRaw(account.Path) is not null)
                        throw new Exception($"Double account flat write. {account.Path}");
                    _writeBatch.SetAccountRaw(account.Path, account.Account);
                }
            }
        }

        _tree.Commit(true, WriteFlags.DisableWAL);
    }

    public void Dispose()
    {
        _writeBatch.Dispose();
        _reader.Dispose();
    }
}
