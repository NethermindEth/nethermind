// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Deposits;

public interface IDepositsProcessor
{
    void ProcessDeposits(Block block, TxReceipt[] receipts, IReleaseSpec spec);
}
