// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;

namespace Nethermind.Consensus.AuRa.Contracts
{
    internal class DataContract<T> : IDataContract<T>
    {
        public delegate bool TryGetChangesFromBlockDelegate(BlockHeader blockHeader, TxReceipt[] receipts, out IEnumerable<T> items);

        private readonly Func<BlockHeader, IEnumerable<T>> _getAll;
        private readonly TryGetChangesFromBlockDelegate _tryGetChangesFromBlock;

        public DataContract(
            Func<BlockHeader, IEnumerable<T>> getAll,
            TryGetChangesFromBlockDelegate tryGetChangesFromBlock)
        {
            IncrementalChanges = false;
            _getAll = getAll ?? throw new ArgumentNullException(nameof(getAll));
            _tryGetChangesFromBlock = tryGetChangesFromBlock ?? throw new ArgumentNullException(nameof(tryGetChangesFromBlock));
        }

        public DataContract(
            Func<BlockHeader, IEnumerable<T>> getAll,
            Func<BlockHeader, TxReceipt[], IEnumerable<T>> getChangesFromBlock)
            : this(getAll, GetTryGetChangesFromBlock(getChangesFromBlock))
        {
            IncrementalChanges = true;
        }

        private static TryGetChangesFromBlockDelegate GetTryGetChangesFromBlock(Func<BlockHeader, TxReceipt[], IEnumerable<T>> getChangesFromBlock)
        {
            return (BlockHeader blockHeader, TxReceipt[] receipts, out IEnumerable<T> items) =>
            {
                items = getChangesFromBlock(blockHeader, receipts).ToArray();
                return items.Any();
            };
        }

        public IEnumerable<T> GetAllItemsFromBlock(BlockHeader blockHeader) => _getAll(blockHeader);

        public bool TryGetItemsChangedFromBlock(BlockHeader header, TxReceipt[] receipts, out IEnumerable<T> items) => _tryGetChangesFromBlock(header, receipts, out items);

        public bool IncrementalChanges { get; }
    }
}
