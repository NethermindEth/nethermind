// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Cpu;
using Nethermind.Core.Threading;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.State;

internal sealed partial class PersistentStorageProvider
{
    private partial void UpdateRootHashes(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch)
    {
        if (_toUpdateRoots.Count >= 3)
            UpdateRootHashesMultiThread(writeBatch);
        else
            UpdateRootHashesSingleThread(writeBatch);
    }

    private void UpdateRootHashesMultiThread(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch)
    {
        // We can recalculate the roots in parallel as they are all independent tries
        using ArrayPoolList<(
            AddressAsKey Key, PerContractState ContractState,
            IWorldStateScopeProvider.IStorageWriteBatch WriteBatch
            )> storages = new(_toUpdateRoots.Count);

        foreach (KeyValuePair<AddressAsKey, bool> kv in _toUpdateRoots)
        {
            if (!kv.Value) continue;
            if (!_storages.TryGetValue(kv.Key, out PerContractState? contractState))
            {
                Debug.Fail($"Storage root marked changed for {kv.Key} but no contract state is present");
                continue;
            }
            storages.Add((
                kv.Key,
                contractState,
                writeBatch.CreateStorageWriteBatch(kv.Key, contractState.EstimatedChanges)));
        }

        if (storages.Count == 0) return;

        // Schedule larger changes first to help balance the work
        storages.AsSpan().Sort(static (a, b) => b.ContractState.EstimatedChanges.CompareTo(a.ContractState.EstimatedChanges));

        using ParallelUnbalancedWork.WorkerScope workers = ParallelUnbalancedWork.BeginWorkerScope(Environment.ProcessorCount);
        ParallelUnbalancedWork.For(
            0,
            storages.Count,
            RuntimeInformation.ParallelOptionsLogicalCores,
            (storages, toUpdateRoots: _toUpdateRoots, writes: 0, skips: 0),
            static (i, state) =>
            {
                ref (AddressAsKey Key, PerContractState ContractState, IWorldStateScopeProvider.IStorageWriteBatch WriteBatch) kvp = ref state.storages.GetRef(i);
                (int writes, int skipped) = kvp.ContractState.ProcessStorageChanges(kvp.WriteBatch);

                if (writes == 0)
                {
                    // Mark as no changes; we set as false rather than removing so
                    // as not to modify the non-concurrent collection without synchronization
                    state.toUpdateRoots[kvp.Key] = false;
                }
                else
                {
                    state.writes += writes;
                }

                state.skips += skipped;

                return state;
            },
            (state) => ReportMetrics(state.writes, state.skips)
        );
    }

    private sealed partial class PerContractState
    {
        [SkipLocalsInit]
        private partial (int writes, int skipped) WriteChanges(IWorldStateScopeProvider.IStorageWriteBatch storageWriteBatch)
        {
            int writes = 0;
            int skipped = 0;

            using ArrayPoolListRef<UInt256> deferredDeletes = new(0);

            foreach (KeyValuePair<SlotKey, StorageChangeTrace> kvp in BlockChange)
            {
                UInt256 after = kvp.Value.After;
                if (kvp.Value.Before != after || kvp.Value.IsInitialValue)
                {
                    if (after.IsZero)
                    {
                        deferredDeletes.Add(kvp.Key);
                    }
                    else
                    {
                        // Safe while enumerating: this only overwrites the existing key, never adds or removes.
                        BlockChange[kvp.Key] = new(after, after);
                        storageWriteBatch.Set(kvp.Key, in after);

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
