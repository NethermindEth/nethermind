// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Consensus.AuRa.Contracts.DataStore
{
    public interface IContractDataStoreCollection<T>
    {
        void Clear();

        IEnumerable<T> GetSnapshot();

        void Insert(IEnumerable<T> items, bool inFront = false);

        void Remove(IEnumerable<T> items);
    }
}
