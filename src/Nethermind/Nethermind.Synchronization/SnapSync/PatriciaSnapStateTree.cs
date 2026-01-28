// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.SnapSync;

public class PatriciaSnapStateTree(StateTree tree) : ISnapStateTree
{
    public Hash256 RootHash { get => tree.RootHash; set => tree.RootHash = value; }
    public TrieNode? RootRef { get => tree.RootRef; set => tree.RootRef = value; }
    public IScopedTrieStore TrieStore => tree.TrieStore;

    public void BulkSet(in ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries, PatriciaTree.Flags flags) =>
        tree.BulkSet(entries, flags);

    public void UpdateRootHash() => tree.UpdateRootHash();

    public void Commit(bool skipRoot, WriteFlags writeFlags) =>
        tree.Commit(skipRoot, writeFlags);

    public bool Set(in ValueHash256 path, Account account) =>
        tree.Set(path, account) is not null;
}
