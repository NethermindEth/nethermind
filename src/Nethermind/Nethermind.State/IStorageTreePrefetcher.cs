// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Trie;

namespace Nethermind.State;

/// <summary>Resolves the trie nodes a storage bulk write is about to read, before it runs.</summary>
public interface IStorageTreePrefetcher
{
    /// <summary>Loads nodes only: nothing in <paramref name="tree"/> or <paramref name="entries"/> may change.</summary>
    /// <param name="tree">The tree the write goes into, at the root it starts from.</param>
    /// <param name="entries">The writes, in no particular order, keys hashed; a delete has an empty value.</param>
    void Prefetch(StorageTree tree, ReadOnlySpan<PatriciaTree.BulkSetEntry> entries);
}
