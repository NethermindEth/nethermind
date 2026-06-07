// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
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
        using ArrayPoolList<StorageRootUpdate> storages = new(_toUpdateRoots.Count);
        foreach (KeyValuePair<AddressAsKey, bool> kvp in _toUpdateRoots)
        {
            if (!kvp.Value || !_storages.TryGetValue(kvp.Key, out PerContractState? contractState)) continue;

            storages.Add(new StorageRootUpdate(kvp.Key, contractState));
        }

        // Schedule larger changes first to help balance the work.
        storages.Sort(static (a, b) => b.ContractState.EstimatedChanges.CompareTo(a.ContractState.EstimatedChanges));

        for (int i = 0; i < storages.Count; i++)
        {
            ref StorageRootUpdate storage = ref storages.GetRef(i);
            storage.WriteBatch = writeBatch.CreateStorageWriteBatch(storage.Key, storage.ContractState.EstimatedChanges);
        }

        ParallelUnbalancedWork.For(
            0,
            storages.Count,
            RuntimeInformation.ParallelOptionsPhysicalCoresUpTo16,
            (storages, writes: 0, skips: 0),
            static (i, state) =>
            {
                ref StorageRootUpdate kvp = ref state.storages.GetRef(i);
                (int writes, int skipped) = kvp.ContractState.ProcessStorageChanges(kvp.WriteBatch!);

                state.writes += writes;

                state.skips += skipped;

                return state;
            },
            (state) => ReportMetrics(state.writes, state.skips)
        );
    }

    private struct StorageRootUpdate(AddressAsKey key, PerContractState contractState)
    {
        public readonly AddressAsKey Key = key;
        public readonly PerContractState ContractState = contractState;
        public IWorldStateScopeProvider.IStorageWriteBatch? WriteBatch;
    }
}
