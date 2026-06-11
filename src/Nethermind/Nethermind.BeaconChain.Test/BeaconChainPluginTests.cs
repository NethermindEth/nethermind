// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test;

public class BeaconChainPluginTests
{
    [Test]
    public void Plugin_wiring_resolves_service_and_database_and_is_gated_by_config()
    {
        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new BeaconChainModule())
            .AddSingleton<IBeaconChainConfig>(new BeaconChainConfig())
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton(Substitute.For<IEngineRpcModule>()) // registered by MergePlugin in production
            .AddSingleton<ITimestamper>(Timestamper.Default) // registered by NethermindModule in production
            .AddSingleton<IDbFactory, MemDbFactory>();

        using IContainer container = builder.Build();

        Assert.Multiple(() =>
        {
            Assert.That(container.Resolve<BeaconChainService>(), Is.Not.Null);
            Assert.That(container.Resolve<IColumnsDb<BeaconChainDbColumns>>(), Is.Not.Null);
            // Pulls the whole P2P graph: peer pool -> peer manager -> libp2p host -> status/metadata sources.
            Assert.That(container.Resolve<RangeSync>(), Is.Not.Null);
            Assert.That(new BeaconChainPlugin(new BeaconChainConfig()).Enabled, Is.False);
            Assert.That(new BeaconChainPlugin(new BeaconChainConfig { Enabled = true }).Enabled, Is.True);
        });
    }
}
