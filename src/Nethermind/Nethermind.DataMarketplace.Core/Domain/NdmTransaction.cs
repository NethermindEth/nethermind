// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class NdmTransaction
    {
        public Transaction Transaction { get; }
        public bool IsPending { get; }
        public long? BlockNumber { get; }
        public Keccak? BlockHash { get; }
        public long? GasUsed { get; set; }

        public NdmTransaction(Transaction transaction, bool isPending, long? blockNumber, Keccak? blockHash, long? gasUsed)
        {
            Transaction = transaction;
            IsPending = isPending;
            BlockNumber = blockNumber;
            BlockHash = blockHash;
            GasUsed = gasUsed;
        }
    }
}
