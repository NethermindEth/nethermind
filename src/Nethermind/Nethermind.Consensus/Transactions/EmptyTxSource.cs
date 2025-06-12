// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Producers;
using Nethermind.Core;

namespace Nethermind.Consensus.Transactions
{
    public class EmptyTxSource : ITxSource
    {
        private EmptyTxSource() { }

        public bool SupportsBlobs => false;

        public static ITxSource Instance { get; } = new EmptyTxSource();

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes, bool filterSource)
        {
            return Array.Empty<Transaction>();
        }
    }
}
