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
        /// the guest reaches each entry's key and value in place instead. The host keeps the pair walk.
        /// </remarks>
        [SkipLocalsInit]
        private partial (int writes, int skipped) WriteChanges(IWorldStateScopeProvider.IStorageWriteBatch storageWriteBatch)
        {
            int writes = 0;
            int skipped = 0;

            using ArrayPoolListRef<UInt256> deferredDeletes = new(0);

            OptimizedDictionary<SlotKey, StorageChangeTrace>.Enumerator entries = BlockChange.GetEnumerator();
            while (entries.MoveNext())
            {
                UInt256 key = entries.CurrentKey;
                ref StorageChangeTrace change = ref entries.CurrentValue;
                UInt256 after = change.After;
                if (change.Before != after || change.IsInitialValue)
                {
                    if (after.IsZero)
                    {
                        deferredDeletes.Add(key);
                    }
                    else
                    {
                        // Safe while enumerating: this only overwrites the existing key, never adds or removes.
                        change.Set(after, after, isInitialValue: false);
                        storageWriteBatch.Set(key, in after);

                        writes++;
                    }
                }
                else
                {
                    skipped++;
                }
            }

            writes += WriteDeletes(deferredDeletes.AsSpan(), storageWriteBatch);
            return (writes, skipped);
        }
    }
}
