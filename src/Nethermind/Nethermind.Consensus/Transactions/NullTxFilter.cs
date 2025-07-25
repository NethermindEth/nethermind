// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Transactions
{
    public class NullTxFilter : ITxFilter
    {
        public AcceptTxResult IsAllowed(Transaction tx, BlockHeader parentHeader, IReleaseSpec spec) => AcceptTxResult.Accepted;

        public static readonly NullTxFilter Instance = new();
    }
}
