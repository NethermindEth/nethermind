// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Cpu;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Evm.State;

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
            )> storages = _storages
                // Only consider contracts that actually have pending changes
                .Where(kv => _toUpdateRoots.TryGetValue(kv.Key, out bool hasChanges) && hasChanges)
                // Schedule larger changes first to help balance the work
                .OrderByDescending(kv => kv.Value.EstimatedChanges)
                .Select((kv) => (
                    kv.Key,
                    kv.Value,
                    writeBatch.CreateStorageWriteBatch(kv.Key, kv.Value.EstimatedChanges)
                ))
                .ToPooledList(_storages.Count);

        ParallelUnbalancedWork.For(
            0,
            storages.Count,
            RuntimeInformation.ParallelOptionsPhysicalCoresUpTo16,
            (storages, toUpdateRoots: _toUpdateRoots, writes: 0, skips: 0),
            static (i, state) =>
            {
                ref var kvp = ref state.storages.GetRef(i);
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
}
