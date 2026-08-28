// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.HealthChecks;
using Nethermind.JsonRpc;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
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

        Assert.That(enabledProvider.GetJsonRpcCapabilities()[method].IsEnabled(), Is.True);
        Assert.That(disabledProvider.GetJsonRpcCapabilities()[method].IsEnabled(), Is.False);
    }
}
