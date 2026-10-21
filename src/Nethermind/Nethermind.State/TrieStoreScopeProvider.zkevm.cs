// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Collections;

namespace Nethermind.State;

public partial class TrieStoreScopeProvider
{
    private partial class WorldStateWriteBatch
    {
        private readonly Queue<(AddressAsKey, Hash256)> _dirtyStorageTree = new();
    }

    public partial class StorageTreeBulkWriteBatch
    {
        /// <summary>Never apply the writes through <c>PatriciaTree.BulkSet</c>.</summary>
        /// <remarks>See <c>StateTree.WriteAccounts</c> for why one set at a time is cheaper here. The guest's
        /// <c>PatriciaTree.IsUnchangedPendingLevel</c> relies on its writes going one at a time.</remarks>
        private const int BulkWriteThreshold = int.MaxValue;
    }
}
