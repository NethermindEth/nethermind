// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Sync;

/// <summary>
/// ISnapStateTree adapter wrapping StateTree for flat snap sync.
/// </summary>
public class FlatSnapStateTree(StateTree tree) : ISnapStateTree
{
    public Hash256 RootHash { get => tree.RootHash; set => tree.RootHash = value; }

    public void SetRootFromProof(TrieNode root) => tree.RootRef = root;

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak) =>
        false; // Snap sync builds new state from scratch; force all nodes to be written

    public void BulkSet(in ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries, PatriciaTree.Flags flags) =>
        tree.BulkSet(entries, flags);

    public void UpdateRootHash() => tree.UpdateRootHash();

    public void Commit(bool skipRoot, WriteFlags writeFlags) => tree.Commit(skipRoot, writeFlags);

    public bool Set(in ValueHash256 path, Account account) => tree.Set(path, account) is not null;
}
