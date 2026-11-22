// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

/// <summary>Checks that the methods taking the dispatcher's token hand it to the blockchain bridge, which no longer applies a timeout of its own.</summary>
public class DispatcherTokenForwardingTests
{
    private static readonly object Call = new { to = TestItem.AddressA };
    private static readonly object SimulatePayload = new { blockStateCalls = new[] { new { calls = new[] { Call } } } };

    private static IEnumerable<TestCaseData> Requests()
    {
        yield return Case("eth_call", static chain => chain.TestEthRpc("eth_call", Call, "latest"));
        yield return Case("eth_estimateGas", static chain => chain.TestEthRpc("eth_estimateGas", Call, "latest"));
        yield return Case("eth_createAccessList", static chain => chain.TestEthRpc("eth_createAccessList", Call, "latest"));
        yield return Case("eth_simulateV1", static chain => chain.TestEthRpc("eth_simulateV1", SimulatePayload, "latest"));
        yield return Case("debug_simulateV1", static chain => RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_simulateV1", SimulatePayload, "latest"));
        yield return Case("trace_simulateV1", static chain => RpcTest.TestSerializedRequest(chain.TraceRpcModule, "trace_simulateV1", SimulatePayload, "latest"));
        yield return Case("proof_call", static chain => RpcTest.TestSerializedRequest(chain.ProofRpcModule, "proof_call", Call, "latest"));

        static TestCaseData Case(string method, Func<TestRpcBlockchain, Task<string>> send) => new TestCaseData(send).SetArgDisplayNames(method);
    }

    [TestCaseSource(nameof(Requests))]
    public async Task Method_hands_the_dispatcher_token_to_the_bridge(Func<TestRpcBlockchain, Task<string>> send)
    {
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        bridge.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(bridge).Build();
        bridge.ClearReceivedCalls();
        TimeoutTestHelper.TrackingCancellationTokenSource timeout = TimeoutTestHelper.RentTrackingTimeoutSourceForNextRequest();

        try
        {
            // The substitute answers nothing, so the request fails after the bridge call; only the token the bridge got matters.
            await send(chain);
            CancellationToken[] tokens = [.. bridge.ReceivedCalls().SelectMany(static call => call.GetArguments()).OfType<CancellationToken>()];
            Assert.That(tokens, Is.Not.Empty.And.All.EqualTo(timeout.Token));
        }
        finally
        {
            TimeoutTestHelper.DisposeIfNotAlreadyObserved(timeout);
        }
    }
}
