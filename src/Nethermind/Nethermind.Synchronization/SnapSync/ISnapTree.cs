// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.Synchronization.SnapSync;

/// <summary>
/// Base interface for snap sync tree operations used in FillBoundaryTree.
/// </summary>
public interface ISnapTree : IDisposable
{
    Hash256 RootHash { get; }

    void SetRootFromProof(TrieNode root);
    bool IsPersisted(in TreePath path, in ValueHash256 keccak);
    void BulkSetAndUpdateRootHash(in ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries);
    void Commit(ValueHash256 upperBound);
}
