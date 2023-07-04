// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public interface IDataContract<T>
    {
        /// <summary>
        /// Gets all items in contract from block.
        /// </summary>
        /// <param name="blockHeader"></param>
        /// <returns></returns>
        IEnumerable<T> GetAllItemsFromBlock(BlockHeader blockHeader);

        /// <summary>
        /// Gets item changed in contract in that block.
        /// </summary>
        /// <param name="header"></param>
        /// <param name="receipts"></param>
        /// <param name="items"></param>
        /// <returns></returns>
        bool TryGetItemsChangedFromBlock(BlockHeader header, TxReceipt[] receipts, out IEnumerable<T> items);

        /// <summary>
        /// If changes in blocks are incremental.
        /// If 'true' values we extract from receipts are changes to be merged with previous state.
        /// If 'false' values we extract from receipts overwrite previous state.
        /// </summary>
        bool IncrementalChanges { get; }
    }
}
