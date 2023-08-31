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

namespace Nethermind.Consensus.AuRa.Transactions
{
    public abstract class CompareTxByPriorityBase : IComparer<Transaction>
    {
        private readonly IContractDataStore<Address> _sendersWhitelist;
        private readonly IDictionaryContractDataStore<TxPriorityContract.Destination> _priorities;
        private Keccak _blockHash;
        private ISet<Address> _sendersWhiteListSet;

        public CompareTxByPriorityBase(IContractDataStore<Address> sendersWhitelist, // expected HashSet based
            IDictionaryContractDataStore<TxPriorityContract.Destination> priorities) // expected SortedList based)
        {
            _sendersWhitelist = sendersWhitelist ?? throw new ArgumentNullException(nameof(sendersWhitelist));
            _priorities = priorities ?? throw new ArgumentNullException(nameof(priorities));
        }

        protected abstract BlockHeader BlockHeader { get; }

        public UInt256 GetPriority(Transaction tx) =>
            _priorities.TryGetValue(BlockHeader, tx, out var destination)
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
                _blockHash = blockHeader.Hash;
            }
        }

        public int Compare(Transaction x, Transaction y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (ReferenceEquals(null, y)) return 1;
            if (ReferenceEquals(null, x)) return -1;

            // we already have nonce ordered by previous code, we don't deal with it here

            // first order by whitelisted
            int whitelistedComparision = IsWhiteListed(y).CompareTo(IsWhiteListed(x));
            if (whitelistedComparision != 0) return whitelistedComparision;

            // then order by priority descending
            return GetPriority(y).CompareTo(GetPriority(x));
        }
    }
}
