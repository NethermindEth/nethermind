// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Core;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;

namespace Nethermind.Taiko;

/// <summary>
/// Taiko execution requests processor: sets the empty requests hash on Prague+ blocks
/// without executing system contract calls (deposits, withdrawals, consolidations),
/// since those L1 system contracts don't exist on Taiko L2.
/// </summary>
public class TaikoExecutionRequestsProcessor : IExecutionRequestsProcessor
{
    public void ProcessExecutionRequests(Block block, IWorldState state, TxReceipt[] receipts, IReleaseSpec spec)
    {
        if (!spec.RequestsEnabled || block.IsGenesis)
            return;

        block.ExecutionRequests = ExecutionRequestExtensions.EmptyRequests;
        block.Header.RequestsHash = ExecutionRequestExtensions.EmptyRequestsHash;
    }
}
