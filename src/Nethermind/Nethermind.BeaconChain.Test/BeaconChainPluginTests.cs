// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.BeaconChain.Storage;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
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
            .AddSingleton<IDbFactory, MemDbFactory>();

        using IContainer container = builder.Build();

        Assert.Multiple(() =>
        {
            Assert.That(container.Resolve<BeaconChainService>(), Is.Not.Null);
            Assert.That(container.Resolve<IColumnsDb<BeaconChainDbColumns>>(), Is.Not.Null);
            Assert.That(new BeaconChainPlugin(new BeaconChainConfig()).Enabled, Is.False);
            Assert.That(new BeaconChainPlugin(new BeaconChainConfig { Enabled = true }).Enabled, Is.True);
        });
    }
}
