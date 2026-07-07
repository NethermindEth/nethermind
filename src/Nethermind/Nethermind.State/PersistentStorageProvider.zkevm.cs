// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.State;

namespace Nethermind.State;

internal sealed partial class PersistentStorageProvider
{
    private partial void UpdateRootHashes(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch) =>
        UpdateRootHashesSingleThread(writeBatch);

    private partial void UpdateRootHashes(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch, Nethermind.Core.Collections.ArrayPoolList<Nethermind.Core.AddressAsKey> keys)
    {
        foreach (Nethermind.Core.AddressAsKey key in keys.AsSpan())
        {
            if (_storages.TryGetValue(key, out PerContractState contractState))
            {
                contractState.ProcessStorageChanges(writeBatch.CreateStorageWriteBatch(key, contractState.EstimatedChanges));
            }
        }
    }
}
