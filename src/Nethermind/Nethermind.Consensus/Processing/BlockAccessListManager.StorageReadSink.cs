// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Evm.State;

namespace Nethermind.Consensus.Processing;

public partial class BlockAccessListManager
{
    private sealed class StorageReadSink(BalReadStoragePlan plan, PreBlockCaches caches) : IWorldStateScopeProvider.IAsyncBalReaderSink
    {
        public void OnAccountRead(Address address, Account? account) => caches.StateCache.Set(address, account);

        public bool StillNeeded(Address address, out Account? account) => !caches.StateCache.TryGetValue(address, out account);

        public void OnStorageRead(in StorageCell cell, byte[] value)
        {
            if (plan.TryGetOrdinal(cell, out int ordinal)) plan.StorageValues!.Set(ordinal, value);
            else caches.StorageCache.Set(cell, value);
        }

        public bool StillNeeded(in StorageCell cell)
        {
            if (!plan.TryGetOrdinal(cell, out int ordinal)) return !caches.StorageCache.TryGetValue(cell, out _);
            if (plan.StorageValues!.TryGet(ordinal, out _)) return false;
            if (!caches.StorageCache.TryGetValue(cell, out byte[]? value)) return true;
            plan.StorageValues.Set(ordinal, value);
            return false;
        }
    }
}
