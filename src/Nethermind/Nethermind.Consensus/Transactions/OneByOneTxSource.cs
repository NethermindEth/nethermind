// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Producers;
using Nethermind.Core;

namespace Nethermind.Consensus.Transactions
{
    public class OneByOneTxSource(ITxSource txSource) : ITxSource
    {
        private readonly ITxSource _txSource = txSource;

        public bool SupportsBlobs => _txSource.SupportsBlobs;

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, BlockHeader targetBlock, ulong gasLimit, PayloadAttributes? payloadAttributes, bool filterSource)
        {
            foreach (Transaction transaction in _txSource.GetTransactions(parent, targetBlock, gasLimit, payloadAttributes, filterSource))
            {
                yield return transaction;
                break;
            }
        }
    }
}
