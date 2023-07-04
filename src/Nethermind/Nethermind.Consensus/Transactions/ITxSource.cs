// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Consensus.Transactions
{
    public interface ITxSource
    {
        IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit);
    }


}
