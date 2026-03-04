// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Trie;

namespace Nethermind.Synchronization.SnapSync;

public class PatriciaSnapStateTree(StateTree tree, SnapUpperBoundAdapter adapter, INodeStorage nodeStorage) : ISnapTree<PathWithAccount>
{
    public Hash256 RootHash => tree.RootHash;

    public void SetRootFromProof(TrieNode root) => tree.RootRef = root;

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak) =>
        nodeStorage.KeyExists(null, path, keccak);

    public void BulkSetAndUpdateRootHash(IReadOnlyList<PathWithAccount> entries)
    {
        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> bulkEntries = new(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            PathWithAccount account = entries[i];
            Account accountValue = account.Account;
            Rlp rlp = accountValue.IsTotallyEmpty ? StateTree.EmptyAccountRlp : Rlp.Encode(accountValue);
            bulkEntries.Add(new PatriciaTree.BulkSetEntry(account.Path, rlp.Bytes));
            Interlocked.Add(ref Metrics.SnapStateSynced, rlp.Bytes.Length);
        }

        tree.BulkSet(bulkEntries, PatriciaTree.Flags.WasSorted);
        tree.UpdateRootHash();
    }

    public void Commit(ValueHash256 upperBound)
    {
        adapter.UpperBound = upperBound;
        tree.Commit(skipRoot: true, WriteFlags.DisableWAL);
    }

    public void Dispose() { } // No-op - Patricia doesn't own resources
}
