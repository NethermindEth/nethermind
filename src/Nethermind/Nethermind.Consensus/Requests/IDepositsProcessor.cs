// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.ConsensusRequests;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Requests;

public interface IDepositsProcessor
{
    List<Deposit>? ProcessDeposits(Block block, TxReceipt[] receipts, IReleaseSpec spec);
}
