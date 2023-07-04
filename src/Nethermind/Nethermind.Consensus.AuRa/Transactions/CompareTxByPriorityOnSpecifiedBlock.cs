// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class CompareTxByPriorityOnSpecifiedBlock : CompareTxByPriorityBase
    {
        public CompareTxByPriorityOnSpecifiedBlock(
            IContractDataStore<Address> sendersWhitelist,
            IDictionaryContractDataStore<TxPriorityContract.Destination> priorities,
            BlockHeader blockHeader) : base(sendersWhitelist, priorities)
        {
            BlockHeader = blockHeader;
        }

        protected override BlockHeader BlockHeader { get; }
    }
}
