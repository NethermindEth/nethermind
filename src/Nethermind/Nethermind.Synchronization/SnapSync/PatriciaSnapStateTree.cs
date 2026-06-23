// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Trie;

namespace Nethermind.Synchronization.SnapSync;

public class PatriciaSnapStateTree(StateTree tree, SnapUpperBoundAdapter adapter, INodeStorage nodeStorage) : ISnapTree<PathWithAccount>
{
    private readonly NodeStoragePersistedCheckScope _persistedCheckScope = new(nodeStorage);

    public Hash256 RootHash => tree.RootHash;

    public void SetRootFromProof(TrieNode root) => tree.RootRef = root;

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak) =>
        _persistedCheckScope.Current.KeyExists(null, path, keccak);

    public IDisposable BeginPersistedCheckScope() =>
        _persistedCheckScope.Begin();

    public void BulkSetAndUpdateRootHash(IReadOnlyList<PathWithAccount> entries) =>
        ISnapTree<PathWithAccount>.DoBulkSetAndUpdateRootHash(tree, entries);

    public void Commit(ValueHash256 upperBound)
    {
        _persistedCheckScope.Dispose();
        adapter.UpperBound = upperBound;
        tree.Commit(skipRoot: true, WriteFlags.DisableWAL);
    }

    public void Dispose() => _persistedCheckScope.Dispose();
}
