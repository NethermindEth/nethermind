// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.SnapSync;

public class PatriciaSnapStorageTree(StorageTree tree) : ISnapStorageTree
{
    public Hash256 RootHash => tree.RootHash;
    public TrieNode? RootRef { get => tree.RootRef; set => tree.RootRef = value; }
    public IScopedTrieStore TrieStore => tree.TrieStore;

    public void BulkSet(in ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries, PatriciaTree.Flags flags) =>
        tree.BulkSet(entries, flags);

    public void UpdateRootHash() => tree.UpdateRootHash();

    public void Commit(WriteFlags writeFlags) =>
        tree.Commit(writeFlags: writeFlags);
}
