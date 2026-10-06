// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Collections;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.State;

internal sealed partial class PersistentStorageProvider
{
    private partial void UpdateRootHashes(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch) =>
        UpdateRootHashesSingleThread(writeBatch);

    private sealed partial class PerContractState
    {
        /// <remarks>
        /// ILC block-copies each 104-byte pair through corelib's out-of-line Memmove several times per entry, so
        /// the guest reaches each entry's key and value in place instead. The host keeps the pair walk: its dictionary has
        /// no in-place enumerator accessors.
        /// </remarks>
        [SkipLocalsInit]
        private partial (int writes, int skipped) WriteChanges(IWorldStateScopeProvider.IStorageWriteBatch storageWriteBatch)
        {
            int writes = 0;
            int skipped = 0;

            // Deletes are likely rare, so start with zero capacity; the pooled array is rented only on first Add.
            using ArrayPoolListRef<UInt256> deferredDeletes = new(0);

            OptimizedDictionary<SlotKey, StorageChangeTrace>.Enumerator entries = BlockChange.GetEnumerator();
            while (entries.MoveNext())
            {
                UInt256 key = entries.CurrentKey;
                ref StorageChangeTrace change = ref entries.CurrentValue;
                if (!change.IsPendingWrite)
                {
                    skipped++;
                }
                else if (CommitAndWriteUnlessDelete(key, ref change, storageWriteBatch))
                {
                    writes++;
                }
                else
                {
                    deferredDeletes.Add(key);
                }
            }

            writes += WriteDeletes(deferredDeletes.AsSpan(), storageWriteBatch);
            return (writes, skipped);
        }
    }
}
