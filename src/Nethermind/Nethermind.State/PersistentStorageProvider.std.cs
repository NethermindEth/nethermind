// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Cpu;
using Nethermind.Core.Threading;
using Nethermind.Evm.State;

namespace Nethermind.State;

internal sealed partial class PersistentStorageProvider
{
    // Long-tail-first scheduling for the parallel UpdateRootHashes pass.
    private readonly struct StorageWorkEntryByEstimatedChangesDescendingComparer
        : IComparer<(AddressAsKey Key, PerContractState ContractState, IWorldStateScopeProvider.IStorageWriteBatch WriteBatch)>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(
            (AddressAsKey Key, PerContractState ContractState, IWorldStateScopeProvider.IStorageWriteBatch WriteBatch) left,
            (AddressAsKey Key, PerContractState ContractState, IWorldStateScopeProvider.IStorageWriteBatch WriteBatch) right) =>
            right.ContractState.EstimatedChanges.CompareTo(left.ContractState.EstimatedChanges);
    }

    private partial void UpdateRootHashes(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch) =>
        UpdateRootHashesMultiThread(writeBatch);

    private void UpdateRootHashesMultiThread(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch)
    {
        using ArrayPoolList<(
            AddressAsKey Key, PerContractState ContractState,
            IWorldStateScopeProvider.IStorageWriteBatch WriteBatch
            )> storages = new(_toUpdateRoots.Count);

        foreach (KeyValuePair<AddressAsKey, bool> kv in _toUpdateRoots)
        {
            if (!kv.Value || !_storages.TryGetValue(kv.Key, out PerContractState? state))
            {
                continue;
            }

            storages.Add((
                kv.Key,
                state,
                writeBatch.CreateStorageWriteBatch(kv.Key, state.EstimatedChanges)));
        }

        if (storages.Count == 0)
        {
            return;
        }

        storages.Sort<StorageWorkEntryByEstimatedChangesDescendingComparer>(default);

        if (storages.Count == 1)
        {
            ref (AddressAsKey Key, PerContractState ContractState, IWorldStateScopeProvider.IStorageWriteBatch WriteBatch) kvp = ref storages.GetRef(0);
            (int writes, int skipped) = kvp.ContractState.ProcessStorageChanges(kvp.WriteBatch);
            ReportMetrics(writes, skipped);
            return;
        }

        ParallelUnbalancedWork.For(
            0,
            storages.Count,
            RuntimeInformation.ParallelOptionsPhysicalCoresUpTo16,
            (storages, writes: 0, skips: 0),
            static (i, state) =>
            {
                ref (AddressAsKey Key, PerContractState ContractState, IWorldStateScopeProvider.IStorageWriteBatch WriteBatch) kvp = ref state.storages.GetRef(i);
                (int writes, int skipped) = kvp.ContractState.ProcessStorageChanges(kvp.WriteBatch);

                state.writes += writes;
                state.skips += skipped;

                return state;
            },
            (state) => ReportMetrics(state.writes, state.skips)
        );
    }
}
