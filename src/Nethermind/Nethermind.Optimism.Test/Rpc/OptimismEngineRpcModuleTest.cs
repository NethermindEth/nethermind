// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.SszRest.Handlers;
using Nethermind.Optimism.ProtocolVersion;
using Nethermind.Optimism.Rpc;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace Nethermind.Optimism.Test.Rpc;

[Parallelizable(ParallelScope.All)]
public class OptimismEngineRpcModuleTest
{
    private static IEnumerable<(OptimismProtocolVersion, OptimismSuperchainSignal, bool behindRecommended, bool behindRequired)> SignalSuperchainV1Cases()
    {
        yield return (
            new OptimismProtocolVersion.V0(new byte[8], 3, 0, 0, 0),
            new OptimismSuperchainSignal(
                Recommended: new OptimismProtocolVersion.V0(new byte[8], 2, 0, 0, 0),
                Required: new OptimismProtocolVersion.V0(new byte[8], 1, 0, 0, 0)),
            behindRecommended: false,
            behindRequired: false
        );

        yield return (
            new OptimismProtocolVersion.V0(new byte[8], 2, 0, 0, 0),
            new OptimismSuperchainSignal(
                Recommended: new OptimismProtocolVersion.V0(new byte[8], 2, 0, 0, 0),
                Required: new OptimismProtocolVersion.V0(new byte[8], 1, 0, 0, 0)),
            behindRecommended: false,
            behindRequired: false
        );

        yield return (
            new OptimismProtocolVersion.V0(new byte[8], 2, 0, 0, 0),
            new OptimismSuperchainSignal(
                Recommended: new OptimismProtocolVersion.V0(new byte[8], 3, 0, 0, 0),
                Required: new OptimismProtocolVersion.V0(new byte[8], 1, 0, 0, 0)),
            behindRecommended: true,
            behindRequired: false
        );

        yield return (
            new OptimismProtocolVersion.V0(new byte[8], 1, 0, 0, 0),
            new OptimismSuperchainSignal(
                Recommended: new OptimismProtocolVersion.V0(new byte[8], 2, 0, 0, 0),
                Required: new OptimismProtocolVersion.V0(new byte[8], 1, 0, 0, 0)),
            behindRecommended: true,
            behindRequired: false
        );

        yield return (
            new OptimismProtocolVersion.V0(new byte[8], 1, 0, 0, 0),
            new OptimismSuperchainSignal(
                Recommended: new OptimismProtocolVersion.V0(new byte[8], 3, 0, 0, 0),
                Required: new OptimismProtocolVersion.V0(new byte[8], 2, 0, 0, 0)),
            behindRecommended: true,
            behindRequired: true
        );
    }

    [TestCaseSource(nameof(SignalSuperchainV1Cases))]
    public void SignalSuperchainV1_ComparesRequiredAndRecommendedVersion((OptimismProtocolVersion current, OptimismSuperchainSignal signal, bool behindRecommended, bool behindRequired) testCase)
    {
        OptimismProtocolVersion current = testCase.current;
        OptimismSuperchainSignal signal = testCase.signal;

        IOptimismSignalSuperchainV1Handler handler = Substitute.For<IOptimismSignalSuperchainV1Handler>();
        handler.CurrentVersion.Returns(current);
        IOptimismEngineRpcModule rpcModule = new OptimismEngineRpcModule(Substitute.For<IEngineRpcModule>(), handler);

        _ = rpcModule.engine_signalSuperchainV1(signal);

        handler.Received(testCase.behindRecommended ? 1 : 0).OnBehindRecommended(testCase.signal.Recommended);
        handler.Received(testCase.behindRequired ? 1 : 0).OnBehindRequired(testCase.signal.Required);
    }

    [Test]
    public void SignalSuperchainV1_ReturnsCurrentVersion()
    {
        OptimismProtocolVersion.V0 current = new(new byte[8], 3, 2, 1, 0);
        OptimismSuperchainSignal signal = new(
            Recommended: new OptimismProtocolVersion.V0(new byte[8], 2, 0, 0, 0),
            Required: new OptimismProtocolVersion.V0(new byte[8], 1, 0, 0, 0));

        IOptimismSignalSuperchainV1Handler handler = Substitute.For<IOptimismSignalSuperchainV1Handler>();
        handler.CurrentVersion.Returns(current);
        IOptimismEngineRpcModule rpcModule = new OptimismEngineRpcModule(Substitute.For<IEngineRpcModule>(), handler);

        ResultWrapper<OptimismSignalSuperchainV1Result> result = rpcModule.engine_signalSuperchainV1(signal);

        Assert.That(result.Data, Is.EqualTo(new OptimismSignalSuperchainV1Result(current)));
    }

    [Test]
    public async Task NewPayloadWithWitnessV4_delegates_Optimism_payload()
    {
        IEngineRpcModule engineRpcModule = Substitute.For<IEngineRpcModule>();
        OptimismExecutionPayloadV3 payload = new() { BlockHash = Hash256.Zero, WithdrawalsRoot = Hash256.Zero };
        Hash256?[] blobVersionedHashes = [];
        byte[][] executionRequests = [];
        ResultWrapper<NewPayloadWithWitnessV1Result> expected =
            ResultWrapper<NewPayloadWithWitnessV1Result>.Success(NewPayloadWithWitnessV1Result.FromPayloadStatus(
                new PayloadStatusV1 { Status = PayloadStatus.Syncing }));
        engineRpcModule.engine_newPayloadWithWitnessV4(payload, blobVersionedHashes, Hash256.Zero, executionRequests)
            .Returns(expected);
        IOptimismEngineRpcModule rpcModule = new OptimismEngineRpcModule(
            engineRpcModule, Substitute.For<IOptimismSignalSuperchainV1Handler>());

        ResultWrapper<NewPayloadWithWitnessV1Result> result = await rpcModule.engine_newPayloadWithWitnessV4(
            payload, blobVersionedHashes, Hash256.Zero, executionRequests);

        Assert.That(result, Is.SameAs(expected));
        await engineRpcModule.Received(1).engine_newPayloadWithWitnessV4(
            payload, blobVersionedHashes, Hash256.Zero, executionRequests);
    }

    [Test]
    public void NewPayloadWithWitnessV4_capability_is_enabled_for_Isthmus_without_SSZ_route()
    {
        OptimismReleaseSpec spec = new() { IsOpIsthmusEnabled = true };
        OptimismEngineRpcCapabilitiesProvider provider = new(new TestSingleReleaseSpecProvider(spec));

        Assert.Multiple(() =>
        {
            Assert.That(
                provider.GetJsonRpcCapabilities()[nameof(IEngineRpcModule.engine_newPayloadWithWitnessV4)].IsEnabled(),
                Is.True);
            Assert.That(
                provider.GetSszRestPaths()[SszRestPaths.PostPayloadsWitness].IsEnabled(),
                Is.False);
        });
    }

    private static IEnumerable<(string, string, OptimismProtocolVersion)> SignalSuperchainV1JsonCases()
    {
        yield return (
            """{"recommended":"0x0000000000000000000000000000000000000200000000000000000000000000","required":"0x0000000000000000000000000000000000000100000000000000000000000000"}""",
            """{"protocolVersion":"0x0000000000000000000000000000000000000300000002000000010000000000"}""",
            new OptimismProtocolVersion.V0(new byte[8], 3, 2, 1, 0));

        yield return (
            """{"recommended":"0x0000000000000000000000000000000000000400000000000000000000000000","required":"0x0000000000000000000000000000000000000300000000000000000000000000"}""",
            """{"protocolVersion":"0x00000000000000000000000000000000000002000000090000000a0000000000"}""",
            new OptimismProtocolVersion.V0(new byte[8], 2, 9, 10, 0));
    }
    [TestCaseSource(nameof(SignalSuperchainV1JsonCases))]
    public async Task SignalSuperchainV1_JsonSerialization((string Signal, string Expected, OptimismProtocolVersion Current) testCase)
    {
        IOptimismSignalSuperchainV1Handler handler = Substitute.For<IOptimismSignalSuperchainV1Handler>();
        handler.CurrentVersion.Returns(testCase.Current);
        IOptimismEngineRpcModule rpcModule = new OptimismEngineRpcModule(Substitute.For<IEngineRpcModule>(), handler);

        OptimismSuperchainSignal signal = new EthereumJsonSerializer().Deserialize<OptimismSuperchainSignal>(testCase.Signal);
        string response = await RpcTest.TestSerializedRequest(rpcModule, "engine_signalSuperchainV1", signal);

        Assert.That(JToken.Parse(response), Is.EqualTo(JToken.Parse($$"""{"jsonrpc":"2.0","result":{{testCase.Expected}},"id":67}""")).Using(JToken.EqualityComparer));
    }
}
