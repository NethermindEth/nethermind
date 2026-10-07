// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Trie;

namespace Nethermind.State;

public partial class TrieStoreScopeProvider
{
    private partial class WorldStateWriteBatch
    {
        private static partial void WriteAccounts(StateTree stateTree, Dictionary<AddressAsKey, Account?> accounts)
        {
            if (Avx2.IsSupported && accounts.Count >= KeyHashBatch.MinimumBatchSize)
            {
                stateTree.SetAccounts(accounts);
            }
            else
            {
                using StateTree.StateTreeBulkSetter stateSetter = stateTree.BeginSet(accounts.Count);
                foreach (KeyValuePair<AddressAsKey, Account?> kv in accounts)
                {
                    stateSetter.Set(kv.Key, kv.Value);
                }
            }
        }
    }

    public partial class StorageTreeBulkWriteBatch
    {
        /// <summary>Estimated entries above which the writes are applied together through <see cref="PatriciaTree.BulkSet"/>.</summary>
        private const int BulkWriteThreshold = MIN_ENTRIES_TO_BATCH;
    }
}
