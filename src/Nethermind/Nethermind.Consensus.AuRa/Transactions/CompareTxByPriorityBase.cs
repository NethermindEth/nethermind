// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.TxPool.Comparison;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public abstract class CompareTxByPriorityBase(
        IContractDataStore<Address> sendersWhitelist, // expected HashSet based
        IDictionaryContractDataStore<TxPriorityContract.Destination> priorities)
        : IComparer<Transaction>
    {
        private readonly IContractDataStore<Address> _sendersWhitelist = sendersWhitelist ?? throw new ArgumentNullException(nameof(sendersWhitelist));
        private readonly IDictionaryContractDataStore<TxPriorityContract.Destination> _priorities = priorities ?? throw new ArgumentNullException(nameof(priorities));
        private Hash256 _blockHash;
        private ISet<Address> _sendersWhiteListSet;

        // expected SortedList based)

        protected abstract BlockHeader BlockHeader { get; }

        public UInt256 GetPriority(Transaction tx) =>
            _priorities.TryGetValue(BlockHeader, tx, out TxPriorityContract.Destination destination)
                ? destination.Value
                : UInt256.Zero;

        public bool IsWhiteListed(Transaction tx)
        {
            CheckReloadSendersWhitelist();
            return _sendersWhiteListSet.Contains(tx.SenderAddress);
        }

        private void CheckReloadSendersWhitelist()
        {
            BlockHeader blockHeader = BlockHeader;
            if (blockHeader.Hash != _blockHash)
            {
                _sendersWhiteListSet = _sendersWhitelist.GetItemsFromContractAtBlock(blockHeader).AsSet();
                _blockHash = blockHeader.Hash!;
            }
        }

        public int Compare(Transaction? x, Transaction? y)
        {
            if (ReferenceEquals(x, y)) return TxComparisonResult.Equal;
            if (y is null) return TxComparisonResult.YFirst;
            if (x is null) return TxComparisonResult.XFirst;

            // we already have nonce ordered by previous code, we don't deal with it here

            // first order by whitelisted
            int whitelistedComparison = IsWhiteListed(y).CompareTo(IsWhiteListed(x));
            if (whitelistedComparison != 0) return whitelistedComparison;

            // then order by priority descending
            return GetPriority(y).CompareTo(GetPriority(x));
        }
    }
}
