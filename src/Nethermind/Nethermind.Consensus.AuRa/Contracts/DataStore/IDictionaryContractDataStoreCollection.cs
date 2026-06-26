// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Consensus.AuRa.Contracts.DataStore
{
    public interface IDictionaryContractDataStoreCollection<T> : IContractDataStoreCollection<T> where T : notnull
    {
        bool TryGetValue(T key, [MaybeNullWhen(false)] out T value);
    }
}
