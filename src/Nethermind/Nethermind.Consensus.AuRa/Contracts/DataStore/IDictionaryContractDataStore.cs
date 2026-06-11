// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;

namespace Nethermind.Consensus.AuRa.Contracts.DataStore
{
    public interface IDictionaryContractDataStore<T> : IContractDataStore<T> where T : notnull
    {
        bool TryGetValue(BlockHeader header, T key, [MaybeNullWhen(false)] out T value);
    }
}
