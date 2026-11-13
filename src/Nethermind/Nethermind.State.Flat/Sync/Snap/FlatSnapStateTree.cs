// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Sync.Snap;

/// <summary>
/// ISnapTree adapter for flat snap sync (state).
/// Owns reader (for IsPersisted) and writeBatch (for commits), disposing them on Dispose.
/// </summary>
public class FlatSnapStateTree(IPersistence.IPersistenceReader reader, IPersistence.IWriteBatch writeBatch, bool enableDoubleWriteCheck) : ISnapTree<PathWithAccount>
{
    private readonly SnapNodeRecordTrie _trie = new(reader, writeBatch, address: null, enableDoubleWriteCheck);
    private IReadOnlyList<PathWithAccount>? _pendingEntries;

    public Hash256 RootHash => _trie.RootHash;

    public void SetRootFromProof(IByteArrayList proofs, IReadOnlyList<SnapProofNode> nodes) => _trie.SetRootFromProof(proofs, nodes);

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak) => _trie.IsPersisted(path, keccak);

    public void BulkSetAndUpdateRootHash(IReadOnlyList<PathWithAccount> entries)
    {
        _pendingEntries = entries;
        _trie.BulkSet(entries);
    }

    public bool Commit(ValueHash256 upperBound)
    {
        if (_pendingEntries is not null)
        {
            for (int i = 0; i < _pendingEntries.Count; i++)
            {
                PathWithAccount account = _pendingEntries[i];
                if (account.Path <= upperBound)
                {
                    if (enableDoubleWriteCheck && reader.GetAccountRaw(account.Path) is not null)
                        throw new Exception($"Double account flat write. {account.Path}");
                    Account accountValue = account.Account
                        ?? throw new InvalidOperationException("A SNAP account entry must contain an account value.");
                    writeBatch.SetAccountRaw(account.Path, accountValue);
                }
            }
        }

        return _trie.Commit(upperBound, skipRoot: true);
    }

    public void Dispose()
    {
        _trie.Dispose();
        writeBatch.Dispose();
        reader.Dispose();
    }
}
