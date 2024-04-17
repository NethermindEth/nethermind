// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;
using System.Collections.Generic;

namespace Nethermind.Consensus.AuRa.Deposits;

public class NullDepositsProcessor : IDepositsProcessor
{
    public void ProcessDeposits(Block block, TxReceipt[] receipts, IReleaseSpec spec)
    {
    }
    public static IDepositsProcessor Instance { get; } = new NullDepositsProcessor();
}
