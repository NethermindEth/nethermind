// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Transactions
{
    public class NullTxFilter : ITxFilter
    {
        public AcceptTxResult IsAllowed(Transaction tx, BlockHeader parentHeader) => AcceptTxResult.Accepted;

        public static readonly NullTxFilter Instance = new();
    }
}
