// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Json;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test;
using Nethermind.Merge.Plugin;
using Nethermind.Optimism.ProtocolVersion;
using Nethermind.Optimism.Rpc;
using Nethermind.Serialization.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using NUnit.Framework;

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

        result.Data.Should().Be(new OptimismSignalSuperchainV1Result(current));
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

        JToken.Parse(response).Should().BeEquivalentTo($$"""{"jsonrpc":"2.0","result":{{testCase.Expected}},"id":67}""");
    }
}
