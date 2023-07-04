// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.AuRa.Contracts.DataStore
{
    public interface IDictionaryContractDataStoreCollection<T> : IContractDataStoreCollection<T>
    {
        bool TryGetValue(T key, out T value);
    }
}
