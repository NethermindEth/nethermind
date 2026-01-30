// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Synchronization.SnapSync;

public class PatriciaSnapStateTree(StateTree tree) : ISnapStateTree
{
    public Hash256 RootHash => tree.RootHash;

    public void SetRootFromProof(TrieNode root) => tree.RootRef = root;

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak) =>
        tree.TrieStore.IsPersisted(path, keccak);

    public void Clear() => tree.RootHash = Keccak.EmptyTreeHash;

    public void BulkSet(in ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries, PatriciaTree.Flags flags) =>
        tree.BulkSet(entries, flags);

    public void UpdateRootHash() => tree.UpdateRootHash();

    public void Commit(bool skipRoot, WriteFlags writeFlags) =>
        tree.Commit(skipRoot, writeFlags);

    public void Dispose() { } // No-op - Patricia doesn't own resources
}
