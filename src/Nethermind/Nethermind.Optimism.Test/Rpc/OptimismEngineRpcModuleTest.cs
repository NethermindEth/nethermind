// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin;
using Nethermind.Optimism.Rpc;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.Rpc;

public class OptimismEngineRpcModuleTest
{
    private static IEnumerable<(OptimismProtocolVersion, OptimismSuperchainSignal, bool behindRecommended, bool behindRequired)> SignalSuperchainV1Cases()
    {
        yield return (
            new OptimismProtocolVersion.V0(new byte[8], 3, 0, 0, 0),
            new OptimismSuperchainSignal(
                recommended: new OptimismProtocolVersion.V0(new byte[8], 2, 0, 0, 0),
                required: new OptimismProtocolVersion.V0(new byte[8], 1, 0, 0, 0)),
            behindRecommended: false,
            behindRequired: false
        );

        yield return (
            new OptimismProtocolVersion.V0(new byte[8], 2, 0, 0, 0),
            new OptimismSuperchainSignal(
                recommended: new OptimismProtocolVersion.V0(new byte[8], 2, 0, 0, 0),
                required: new OptimismProtocolVersion.V0(new byte[8], 1, 0, 0, 0)),
            behindRecommended: false,
            behindRequired: false
        );

        yield return (
            new OptimismProtocolVersion.V0(new byte[8], 2, 0, 0, 0),
            new OptimismSuperchainSignal(
                recommended: new OptimismProtocolVersion.V0(new byte[8], 3, 0, 0, 0),
                required: new OptimismProtocolVersion.V0(new byte[8], 1, 0, 0, 0)),
            behindRecommended: true,
            behindRequired: false
        );

        yield return (
            new OptimismProtocolVersion.V0(new byte[8], 1, 0, 0, 0),
            new OptimismSuperchainSignal(
                recommended: new OptimismProtocolVersion.V0(new byte[8], 2, 0, 0, 0),
                required: new OptimismProtocolVersion.V0(new byte[8], 1, 0, 0, 0)),
            behindRecommended: true,
            behindRequired: false
        );

        yield return (
            new OptimismProtocolVersion.V0(new byte[8], 1, 0, 0, 0),
            new OptimismSuperchainSignal(
                recommended: new OptimismProtocolVersion.V0(new byte[8], 3, 0, 0, 0),
                required: new OptimismProtocolVersion.V0(new byte[8], 2, 0, 0, 0)),
            behindRecommended: true,
            behindRequired: true
        );
    }

    [TestCaseSource(nameof(SignalSuperchainV1Cases))]
    public async Task SignalSuperchainV1_ComparesRequiredAndRecommendedVersion((OptimismProtocolVersion current, OptimismSuperchainSignal signal, bool behindRecommended, bool behindRequired) testCase)
    {
        var current = testCase.current;
        var signal = testCase.signal;

        var handler = Substitute.For<IOptimismSuperchainSignalHandler>();
        handler.CurrentVersion.Returns(current);
        IOptimismEngineRpcModule rpcModule = new OptimismEngineRpcModule(Substitute.For<IEngineRpcModule>(), handler);

        var _ = await rpcModule.engine_signalSuperchainV1(signal);

        await handler.Received(testCase.behindRecommended ? 1 : 0).OnBehindRecommended(testCase.signal.Recommended);

        await handler.Received(testCase.behindRequired ? 1 : 0).OnBehindRequired(testCase.signal.Required);
    }

    [Test]
    public async Task SignalSuperchainV1_ReturnsCurrentVersion()
    {
        var current = new OptimismProtocolVersion.V0(new byte[8], 3, 2, 1, 0);
        var signal = new OptimismSuperchainSignal(
            recommended: new OptimismProtocolVersion.V0(new byte[8], 2, 0, 0, 0),
            required: new OptimismProtocolVersion.V0(new byte[8], 1, 0, 0, 0));

        var handler = Substitute.For<IOptimismSuperchainSignalHandler>();
        handler.CurrentVersion.Returns(current);
        IOptimismEngineRpcModule rpcModule = new OptimismEngineRpcModule(Substitute.For<IEngineRpcModule>(), handler);

        ResultWrapper<OptimismProtocolVersion> result = await rpcModule.engine_signalSuperchainV1(signal);

        result.Data.Should().Be(current);
    }
}
