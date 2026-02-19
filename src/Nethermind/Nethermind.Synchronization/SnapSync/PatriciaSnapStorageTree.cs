// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Synchronization.SnapSync;

public class PatriciaSnapStorageTree(StorageTree tree, SnapUpperBoundAdapter adapter, INodeStorage nodeStorage, Hash256 address) : ISnapTree
{
    public Hash256 RootHash => tree.RootHash;

    public void SetRootFromProof(TrieNode root) => tree.RootRef = root;

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak) =>
        nodeStorage.KeyExists(address, path, keccak);

    public void BulkSetAndUpdateRootHash(in ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries)
    {
        tree.BulkSet(entries, PatriciaTree.Flags.WasSorted);
        tree.UpdateRootHash();
    }

    public void Commit(ValueHash256 upperBound)
    {
        adapter.UpperBound = upperBound;
        tree.Commit(writeFlags: WriteFlags.DisableWAL);
    }

    public void Dispose() { } // No-op - Patricia doesn't own resources
}
