// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Sync;

/// <summary>
/// ISnapStorageTree adapter wrapping StorageTree for flat snap sync.
/// </summary>
public class FlatSnapStorageTree(StorageTree tree) : ISnapStorageTree
{
    public Hash256 RootHash => tree.RootHash;

    public void SetRootFromProof(TrieNode root) => tree.RootRef = root;

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak) =>
        false; // Snap sync builds new state from scratch; force all nodes to be written

    public void BulkSet(in ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries, PatriciaTree.Flags flags) =>
        tree.BulkSet(entries, flags);

    public void UpdateRootHash() => tree.UpdateRootHash();

    public void Commit(WriteFlags writeFlags) => tree.Commit(writeFlags: writeFlags);
}
