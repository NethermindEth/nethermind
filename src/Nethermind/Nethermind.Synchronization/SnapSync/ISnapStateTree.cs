// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.SnapSync;

/// <summary>
/// Base interface for snap sync tree operations used in FillBoundaryTree.
/// </summary>
public interface ISnapTree
{
    TrieNode? RootRef { get; set; }
    IScopedTrieStore TrieStore { get; }
}

public interface ISnapStateTree : ISnapTree
{
    Hash256 RootHash { get; set; }

    void BulkSet(in ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries, PatriciaTree.Flags flags);
    void UpdateRootHash();
    void Commit(bool skipRoot, WriteFlags writeFlags);

    // For hasExtraStorage case in AddAccountRange
    bool Set(in ValueHash256 path, Account account);
}
