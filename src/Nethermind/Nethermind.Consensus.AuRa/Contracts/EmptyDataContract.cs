// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Consensus.AuRa.Contracts
{
    internal class EmptyDataContract<T> : IDataContract<T>
    {
        public IEnumerable<T> GetAllItemsFromBlock(BlockHeader blockHeader) => [];

        public bool TryGetItemsChangedFromBlock(BlockHeader header, TxReceipt[] receipts, out IEnumerable<T> items)
        {
            items = [];
            return false;
        }

        public bool IncrementalChanges => true;
    }
}
