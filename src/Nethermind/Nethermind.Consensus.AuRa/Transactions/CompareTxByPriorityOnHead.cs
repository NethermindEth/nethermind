// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Core;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class CompareTxByPriorityOnHead(
        IContractDataStore<Address> sendersWhitelist, // expected HashSet based
        IDictionaryContractDataStore<TxPriorityContract.Destination> priorities, // expected SortedList based
        IBlockTree blockTree) : CompareTxByPriorityBase(sendersWhitelist, priorities)
    {
        private readonly IBlockTree _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));

        protected override BlockHeader BlockHeader => _blockTree.Head.Header;
    }
}
