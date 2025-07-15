// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;

namespace Nethermind.Consensus.ExecutionRequests;

public interface IExecutionRequestsProcessor
{
    public void ProcessExecutionRequests(Block block, IWorldState state, TxReceipt[] receipts, IReleaseSpec spec);
}
