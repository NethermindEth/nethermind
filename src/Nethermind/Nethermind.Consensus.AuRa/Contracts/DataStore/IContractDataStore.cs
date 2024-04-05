// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Consensus.AuRa.Contracts.DataStore
{
    public interface IContractDataStore<out T>
    {
        IEnumerable<T> GetItemsFromContractAtBlock(BlockHeader blockHeader);
    }
}
