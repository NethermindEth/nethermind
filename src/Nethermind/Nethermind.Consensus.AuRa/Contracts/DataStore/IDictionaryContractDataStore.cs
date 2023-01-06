// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.AuRa.Contracts.DataStore
{
    public interface IDictionaryContractDataStore<T> : IContractDataStore<T>
    {
        bool TryGetValue(BlockHeader header, T key, out T value);
    }
}
