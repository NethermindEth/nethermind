// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.HealthChecks;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Specs;
using NUnit.Framework;
using NSubstitute;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [TestCase(true, false, false, true, false)]
    [TestCase(false, true, false, false, true)]
    [TestCase(true, true, false, false, true)]
    [TestCase(true, true, true, false, false)]
    [TestCase(false, false, true, false, false)]
    public async Task GetPayloadV3AndV4_fork_windows_follow_execution_requests(
        bool eip7623Enabled, bool requestsEnabled, bool eip7594Enabled,
        bool expectedV3Valid, bool expectedV4Valid)
    {
        IReleaseSpec releaseSpec = new ReleaseSpec
        {
            IsEip4844Enabled = true,
            IsEip7623Enabled = eip7623Enabled,
            IsEip6110Enabled = requestsEnabled,
            IsEip7002Enabled = requestsEnabled,
            IsEip7251Enabled = requestsEnabled,
            IsEip7594Enabled = eip7594Enabled,
        };
        ISpecProvider specProvider = new TestSingleReleaseSpecProvider(releaseSpec);
        Block block = Build.A.Block.TestObject;
        IPayloadPreparationService payloadService = Substitute.For<IPayloadPreparationService>();
        payloadService.GetPayload(Arg.Any<string>()).Returns(new BlockProductionSnapshot(block, UInt256.Zero));
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(releaseSpec))
            .AddModule(new TestMergeModule())
            .AddSingleton<ISpecProvider>(specProvider)
            .AddSingleton<IPayloadPreparationService>(payloadService)
            .Build();
        IAsyncHandler<byte[], GetPayloadV3Result?> v3Handler = container.Resolve<IAsyncHandler<byte[], GetPayloadV3Result?>>();
        IAsyncHandler<byte[], GetPayloadV4Result?> v4Handler = container.Resolve<IAsyncHandler<byte[], GetPayloadV4Result?>>();
        IRpcCapabilitiesProvider engineRpcCapabilitiesProvider = container.Resolve<IRpcCapabilitiesProvider>();

        ResultWrapper<GetPayloadV3Result?> v3Result = await v3Handler.HandleAsync(new byte[8]);
        ResultWrapper<GetPayloadV4Result?> v4Result = await v4Handler.HandleAsync(new byte[8]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(v3Result.ErrorCode, Is.EqualTo(expectedV3Valid ? 0 : MergeErrorCodes.UnsupportedFork), "getPayloadV3 fork window");
            Assert.That(v4Result.ErrorCode, Is.EqualTo(expectedV4Valid ? 0 : MergeErrorCodes.UnsupportedFork), "getPayloadV4 fork window");
            Assert.That(engineRpcCapabilitiesProvider.GetEngineCapabilities()[nameof(IEngineRpcModule.engine_getPayloadV4)].IsEnabled(), Is.EqualTo(requestsEnabled), "getPayloadV4 capability");
        }
    }

    [TestCase(true, false, true)]
    [TestCase(false, true, false)]
    public void Engine_getBlobsV4_capability_follows_eip7843(bool eip7843Enabled, bool eip7928Enabled, bool expected)
    {
        IReleaseSpec releaseSpec = new ReleaseSpec
        {
            IsEip7843Enabled = eip7843Enabled,
            IsEip7928Enabled = eip7928Enabled,
        };
        ISpecProvider specProvider = new TestSingleReleaseSpecProvider(releaseSpec);
        EngineRpcCapabilitiesProvider engineRpcCapabilitiesProvider = new(specProvider);

        Assert.That(engineRpcCapabilitiesProvider.GetJsonRpcCapabilities()[nameof(IEngineRpcModule.engine_getBlobsV4)].IsEnabled(), Is.EqualTo(expected));
    }

    [TestCase(nameof(IEngineRpcModule.engine_newPayloadV6))]
    [TestCase(nameof(IEngineRpcModule.engine_getInclusionListV1))]
    [TestCase(nameof(IEngineRpcModule.engine_forkchoiceUpdatedV5))]
    [TestCase(nameof(IEngineRpcModule.engine_newPayloadWithWitnessV6))]
    public void Engine_inclusionList_capabilities_follow_eip7805(string method)
    {
        IReleaseSpec enabledSpec = new ReleaseSpec { IsEip7805Enabled = true };
        IReleaseSpec disabledSpec = new ReleaseSpec { IsEip7805Enabled = false };
        EngineRpcCapabilitiesProvider enabledProvider = new(new TestSingleReleaseSpecProvider(enabledSpec));
        EngineRpcCapabilitiesProvider disabledProvider = new(new TestSingleReleaseSpecProvider(disabledSpec));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(enabledProvider.GetJsonRpcCapabilities()[method].IsEnabled(), Is.True);
            Assert.That(disabledProvider.GetJsonRpcCapabilities()[method].IsEnabled(), Is.False);
        }
    }
}
