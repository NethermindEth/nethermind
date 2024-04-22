// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Consensus.Requests;

namespace Nethermind.Consensus.AuRa.Deposits;

public class NullDepositsProcessor : IDepositsProcessor
{
    public List<Deposit>? ProcessDeposits(Block block, TxReceipt[] receipts, IReleaseSpec spec)
    {
        return null;
    }
    public static IDepositsProcessor Instance { get; } = new NullDepositsProcessor();
}
