// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.State.Snap;
using Nethermind.Trie;

namespace Nethermind.Synchronization.SnapSync;

/// <summary>
/// Base interface for snap sync tree operations used in FillBoundaryTree.
/// </summary>
public interface ISnapTree<in TEntry> : IDisposable where TEntry : ISnapEntry
{
    Hash256 RootHash { get; }

    void SetRootFromProof(TrieNode root);
    bool IsPersisted(in TreePath path, in ValueHash256 keccak);
    void BulkSetAndUpdateRootHash(IReadOnlyList<TEntry> entries);
    void Commit(ValueHash256 upperBound);
}
