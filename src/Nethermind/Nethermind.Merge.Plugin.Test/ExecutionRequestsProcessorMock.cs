// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.State;

namespace Nethermind.Merge.Plugin.Test;

public class ExecutionRequestsProcessorMock : IExecutionRequestsProcessor
{
    public static TestExecutionRequest[] depositRequests =
    [
        TestItem.ExecutionRequestA,
        TestItem.ExecutionRequestB,
        TestItem.ExecutionRequestC
    ];

    public static TestExecutionRequest[] withdrawalRequests =
    [
        TestItem.ExecutionRequestD,
        TestItem.ExecutionRequestE,
        TestItem.ExecutionRequestF
    ];

    public static TestExecutionRequest[] consolidationsRequests =
    [
        TestItem.ExecutionRequestG,
        TestItem.ExecutionRequestH,
        TestItem.ExecutionRequestI
    ];

    public static byte[][] Requests
    {
        get
        {
            using ArrayPoolList<byte[]> list = TestExecutionRequestExtensions.GetFlatEncodedRequests(depositRequests, withdrawalRequests, consolidationsRequests);
            return list.ToArray();
        }
    }

    public void ProcessExecutionRequests(Block block, IWorldState state, in BlockExecutionContext blkCtx, TxReceipt[] receipts, IReleaseSpec spec)
    {
        if (block.IsGenesis)
            return;

        block.ExecutionRequests = Requests;
        block.Header.RequestsHash = ExecutionRequestExtensions.CalculateHashFromFlatEncodedRequests(Requests);
    }
}
