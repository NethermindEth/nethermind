// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.State;

namespace Nethermind.Merge.Plugin.Test;

public class ExecutionRequestsProcessorMock : IExecutionRequestsProcessor
{
    public static ExecutionRequest[] Requests =
    [
        TestItem.ExecutionRequestA,
        TestItem.ExecutionRequestB,
        TestItem.ExecutionRequestC,
        TestItem.ExecutionRequestD,
        TestItem.ExecutionRequestE,
        TestItem.ExecutionRequestF,
        TestItem.ExecutionRequestG,
        TestItem.ExecutionRequestH,
        TestItem.ExecutionRequestI
    ];

    public void ProcessExecutionRequests(Block block, IWorldState state, TxReceipt[] receipts, IReleaseSpec spec)
    {
        if (block.IsGenesis)
            return;

        block.ExecutionRequests = new ArrayPoolList<ExecutionRequest>(Requests.Length);
        foreach (var request in Requests)
        {
            block.ExecutionRequests.Add(request);
        }
        block.Header.RequestsHash = Requests.CalculateHash();
    }
}
