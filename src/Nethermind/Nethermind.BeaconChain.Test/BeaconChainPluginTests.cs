// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Network;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test;

public class BeaconChainPluginTests
{
    [Test]
    public void Plugin_wiring_resolves_service_and_database_and_is_gated_by_config()
    {
        IIPResolver ipResolver = Substitute.For<IIPResolver>(); // registered by NetworkModule in production
        ipResolver.Resolve(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IIPResolver.NethermindIp>(new IIPResolver.NethermindIp(IPAddress.Loopback, IPAddress.Loopback)));
        ISpecProvider specProvider = Substitute.For<ISpecProvider>(); // registered by NethermindModule in production
        specProvider.ChainId.Returns(BlockchainIds.Mainnet);
        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new BeaconChainModule())
            .AddSingleton<IBeaconChainConfig>(new BeaconChainConfig())
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton(Substitute.For<IEngineRpcModule>()) // registered by MergePlugin in production
            .AddSingleton<ITimestamper>(Timestamper.Default) // registered by NethermindModule in production
            .AddSingleton(ipResolver)
            .AddSingleton(specProvider)
            .AddSingleton<IDbFactory, MemDbFactory>();

        using IContainer container = builder.Build();

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
    public void Plugin_wiring_fails_loudly_for_an_unsupported_chain_id()
    {
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.ChainId.Returns(BlockchainIds.Sepolia);
        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new BeaconChainModule())
            .AddSingleton<IBeaconChainConfig>(new BeaconChainConfig())
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton(Substitute.For<IEngineRpcModule>())
            .AddSingleton(specProvider);

        using IContainer container = builder.Build();

        // Autofac wraps the factory's exception; the failure must still surface loudly with the chain id.
        DependencyResolutionException wrapped = Assert.Throws<DependencyResolutionException>(
            () => container.Resolve<BeaconChainSpec>())!;
        UnsupportedBeaconNetworkException ex = (UnsupportedBeaconNetworkException)wrapped.GetBaseException();
        Assert.Multiple(() =>
        {
            Assert.That(ex.ChainId, Is.EqualTo(BlockchainIds.Sepolia));
            Assert.That(ex.Message, Does.Contain(BlockchainIds.Sepolia.ToString()));
        });
    }
}
