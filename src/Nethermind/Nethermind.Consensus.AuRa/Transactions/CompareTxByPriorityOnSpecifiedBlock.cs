// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Core;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class CompareTxByPriorityOnSpecifiedBlock(
        IContractDataStore<Address> sendersWhitelist,
        IDictionaryContractDataStore<TxPriorityContract.Destination> priorities,
        BlockHeader blockHeader) : CompareTxByPriorityBase(sendersWhitelist, priorities)
    {
        protected override BlockHeader BlockHeader { get; } = blockHeader;
    }
}
