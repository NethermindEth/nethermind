// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Int256;
using Nethermind.State.Flat.Collections;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>Negative filters over the keys of a committed <see cref="SnapshotContent"/>, keyed by <see cref="HashedKey{T}"/> hash codes.</summary>
/// <remarks>
/// Owned by the <see cref="SnapshotContent"/> and pooled with it, so each build reuses the arrays of an earlier one.
/// </remarks>
internal sealed class SnapshotContentFilters
{
    public readonly SplitBlockFilter Accounts = new();
    public readonly SplitBlockFilter Storages = new();
    public readonly SplitBlockFilter StateNodes = new();
    public readonly SplitBlockFilter StorageNodes = new();

    /// <summary>Rebuilds every filter from the keys of <paramref name="content"/>.</summary>
    public void Build(SnapshotContent content, in SnapshotContentCounts counts)
    {
        Accounts.Reset(counts.Accounts);
        foreach (KeyValuePair<HashedKey<Address>, Account?> entry in content.Accounts) Accounts.Add((uint)entry.Key.GetHashCode());

        Storages.Reset(counts.Storages);
        foreach (KeyValuePair<HashedKey<(Address, UInt256)>, UInt256?> entry in content.Storages) Storages.Add((uint)entry.Key.GetHashCode());

        StateNodes.Reset(counts.StateNodes);
        foreach (KeyValuePair<HashedKey<TreePath>, TrieNode> entry in content.StateNodes) StateNodes.Add((uint)entry.Key.GetHashCode());

        StorageNodes.Reset(counts.StorageNodes);
        content.StorageNodes.AddKeyHashesTo(StorageNodes);
    }
}
