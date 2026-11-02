// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State;

public partial class TrieStoreScopeProvider
{
    private partial class WorldStateWriteBatch
    {
        private readonly ConcurrentQueue<(AddressAsKey, Hash256)> _dirtyStorageTree = new();
    }

    public partial class StorageTreeBulkWriteBatch
    {
        /// <summary>Estimated entries above which the writes are applied together through <see cref="PatriciaTree.BulkSet"/>.</summary>
        private const int BulkWriteThreshold = MIN_ENTRIES_TO_BATCH;
    }
}
