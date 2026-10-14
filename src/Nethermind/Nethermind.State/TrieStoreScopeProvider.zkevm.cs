// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.State;

public partial class TrieStoreScopeProvider
{
    private partial class WorldStateWriteBatch
    {
        /// <remarks>
        /// One at a time, deletions last - the guest's counterpart of the bulk set: on its one core, sorting the entries
        /// and recursing over them costs more than the shared descent saves. Deletions go last, as storage writes do in
        /// <c>ProcessStorageChanges</c>, so a branch is only collapsed once every insert has landed.
        /// </remarks>
        private static partial void WriteAccounts(StateTree stateTree, Dictionary<AddressAsKey, Account?> accounts)
        {
            foreach (KeyValuePair<AddressAsKey, Account?> kv in accounts)
            {
                if (kv.Value is not null) stateTree.Set(kv.Key.Value, kv.Value);
            }

            foreach (KeyValuePair<AddressAsKey, Account?> kv in accounts)
            {
                if (kv.Value is null) stateTree.Set(kv.Key.Value, null);
            }
        }
    }

    public partial class StorageTreeBulkWriteBatch
    {
        /// <summary>Never apply the writes through <c>PatriciaTree.BulkSet</c>.</summary>
        /// <remarks>See <c>WriteAccounts</c> for why one set at a time is cheaper here. The guest's
        /// <c>PatriciaTree.IsUnchangedPendingLevel</c> relies on its writes going one at a time.</remarks>
        private const int BulkWriteThreshold = int.MaxValue;
    }
}
