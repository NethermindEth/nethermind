// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Synchronization.SnapSync;

public class PatriciaSnapStorageTree(StorageTree tree) : ISnapStorageTree
{
    public Hash256 RootHash => tree.RootHash;

    public void SetRootFromProof(TrieNode root) => tree.RootRef = root;

    public void Clear() => tree.RootHash = Keccak.EmptyTreeHash;

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak) =>
        tree.TrieStore.IsPersisted(path, keccak);

    public void BulkSet(in ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries, PatriciaTree.Flags flags) =>
        tree.BulkSet(entries, flags);

    public void UpdateRootHash() => tree.UpdateRootHash();

    public void Commit(WriteFlags writeFlags) =>
        tree.Commit(writeFlags: writeFlags);

    public void Dispose() { } // No-op - Patricia doesn't own resources
}
