// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.State;

namespace Nethermind.State;

internal sealed partial class PersistentStorageProvider
{
    private partial void UpdateRootHashes(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch) =>
        UpdateRootHashesSingleThread(writeBatch);
    private partial void ProcessEarlyRootWorkCore(Nethermind.Core.Collections.ArrayPoolList<EarlyRootWorkItem> work)
    {
        foreach (ref readonly EarlyRootWorkItem item in work.AsSpan())
        {
            item.ContractState.ProcessStorageChanges(item.WriteBatch);
        }
    }
}
