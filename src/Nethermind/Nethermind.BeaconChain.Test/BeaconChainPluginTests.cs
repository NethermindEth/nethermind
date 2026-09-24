// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.BeaconChain.Api;
using Nethermind.Config;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.Core;
using Nethermind.Db;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test;

public class BeaconChainPluginTests
{
    [Test]
    public void Plugin_wiring_resolves_service_and_database_and_is_gated_by_config()
    {
        using IContainer container = BeaconChainTestContainer.Builder().Build();

        Assert.Multiple(() =>
        {
            // Pulls the whole driver graph: orchestrator -> importer factory/engine driver and
            // P2P (peer pool -> peer manager -> libp2p host -> status/metadata sources, discv5).
            Assert.That(container.Resolve<BeaconChainService>(), Is.Not.Null);
            Assert.That(container.Resolve<IColumnsDb<BeaconChainDbColumns>>(), Is.Not.Null);
            Assert.That(container.Resolve<BeaconSyncOrchestrator>(), Is.Not.Null);
            // The spec is derived from the execution layer's chain id, not a separate config knob.
            Assert.That(container.Resolve<BeaconChainSpec>(), Is.SameAs(BeaconChainSpec.Mainnet));
            Assert.That(new BeaconChainPlugin(new BeaconChainConfig()).Enabled, Is.False);
            Assert.That(new BeaconChainPlugin(new BeaconChainConfig { Enabled = true }).Enabled, Is.True);
        });
    }

    [Test]
    public void Plugin_wiring_resolves_the_beacon_api_host()
    {
        using IContainer container = BeaconChainTestContainer.Builder()
            .AddSingleton<IBeaconApiConfig>(new BeaconApiConfig())
            .AddSingleton(Substitute.For<IProcessExitSource>()) // registered by the runner in production
            .Build();

        // The host's own tests build it by hand, which cannot see a missing registration: the
        // discovery container threw at startup once for exactly that reason behind a green suite.
        Assert.That(container.Resolve<BeaconApiHost>(), Is.Not.Null);
    }

    [Test]
    public void Plugin_wiring_fails_loudly_for_an_unsupported_chain_id()
    {
        // A synthetic id, not a real network: naming one here makes the test fail the day we model it.
        const ulong unmodelledChainId = 0xDEADBEEF;
        using IContainer container = BeaconChainTestContainer.Builder(unmodelledChainId).Build();

        // Autofac wraps the factory's exception; the failure must still surface loudly with the chain id.
        DependencyResolutionException wrapped = Assert.Throws<DependencyResolutionException>(
            () => container.Resolve<BeaconChainSpec>())!;
        UnsupportedBeaconNetworkException ex = (UnsupportedBeaconNetworkException)wrapped.GetBaseException();
        Assert.Multiple(() =>
        {
            Assert.That(ex.ChainId, Is.EqualTo(unmodelledChainId));
            Assert.That(ex.Message, Does.Contain(unmodelledChainId.ToString()));
        });
    }
}
