// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Transactions
{
    public interface ITxFilter
    {
        AcceptTxResult IsAllowed(Transaction tx, BlockHeader parentHeader);
    }
}
