// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Producers;
using Nethermind.Core;

namespace Nethermind.Consensus.Transactions
{
    public class OneByOneTxSource : ITxSource
    {
        private readonly ITxSource _txSource;

        public bool SupportsBlobs => _txSource.SupportsBlobs;

        public OneByOneTxSource(ITxSource txSource)
        {
            _txSource = txSource;
        }

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes, bool filterSource)
        {
            foreach (Transaction transaction in _txSource.GetTransactions(parent, gasLimit, payloadAttributes, filterSource))
            {
                yield return transaction;
                break;
            }
        }
    }
}
