// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Autofac;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.Core;
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
        ipResolver.ExternalIp.Returns(IPAddress.Loopback);
        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new BeaconChainModule())
            .AddSingleton<IBeaconChainConfig>(new BeaconChainConfig())
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton(Substitute.For<IEngineRpcModule>()) // registered by MergePlugin in production
            .AddSingleton<ITimestamper>(Timestamper.Default) // registered by NethermindModule in production
            .AddSingleton(ipResolver)
            .AddSingleton<IDbFactory, MemDbFactory>();

        using IContainer container = builder.Build();

        Assert.Multiple(() =>
        {
            // Pulls the whole driver graph: orchestrator -> importer factory/engine driver and
            // P2P (peer pool -> peer manager -> libp2p host -> status/metadata sources, discv5).
            Assert.That(container.Resolve<BeaconChainService>(), Is.Not.Null);
            Assert.That(container.Resolve<IColumnsDb<BeaconChainDbColumns>>(), Is.Not.Null);
            Assert.That(container.Resolve<BeaconSyncOrchestrator>(), Is.Not.Null);
            Assert.That(new BeaconChainPlugin(new BeaconChainConfig()).Enabled, Is.False);
            Assert.That(new BeaconChainPlugin(new BeaconChainConfig { Enabled = true }).Enabled, Is.True);
        });
    }
}
