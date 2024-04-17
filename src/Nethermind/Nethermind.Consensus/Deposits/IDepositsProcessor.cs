// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;
using System.Collections.Generic;

namespace Nethermind.Consensus.Withdrawals;

public interface IDepositsProcessor
{
    void ProcessDeposits(Block block, TxReceipt[] receipts, IReleaseSpec spec);
    void ProcessDeposits(Block block, IEnumerable<Deposit> deposits, IReleaseSpec spec);
}
