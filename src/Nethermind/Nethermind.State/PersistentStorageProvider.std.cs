// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Cpu;
using Nethermind.Core.Threading;
using Nethermind.Evm.State;

namespace Nethermind.State;

internal sealed partial class PersistentStorageProvider
{
    private const int MinParallelStorageRootUpdates = 8;

    private static readonly ParallelOptions[] _parallelOptionsByDegree = CreateParallelOptionsByDegree();

    private static ParallelOptions[] CreateParallelOptionsByDegree()
    {
        int maxDegree = RuntimeInformation.ParallelOptionsPhysicalCoresUpTo16.MaxDegreeOfParallelism;
        ParallelOptions[] options = new ParallelOptions[maxDegree + 1];
        for (int i = 1; i <= maxDegree; i++)
        {
            options[i] = new ParallelOptions { MaxDegreeOfParallelism = i };
        }

        return options;
    }

    private partial void UpdateRootHashes(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch)
    {
        if (_toUpdateRoots.Count >= MinParallelStorageRootUpdates)
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

        foreach (KeyValuePair<AddressAsKey, PerContractState> kv in _storages)
        {
            if (!_toUpdateRoots.TryGetValue(kv.Key, out bool hasChanges) || !hasChanges) continue;
            storages.Add((
                kv.Key,
                kv.Value,
                writeBatch.CreateStorageWriteBatch(kv.Key, kv.Value.EstimatedChanges)));
        }

        if (storages.Count == 0) return;

        // Schedule larger changes first to help balance the work
        storages.AsSpan().Sort(static (a, b) => b.ContractState.EstimatedChanges.CompareTo(a.ContractState.EstimatedChanges));

        ParallelOptions parallelOptions = _parallelOptionsByDegree[Math.Min(storages.Count, _parallelOptionsByDegree.Length - 1)];
        ParallelUnbalancedWork.For(
            0,
            storages.Count,
            parallelOptions,
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
}
