// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Autofac;
using Nethermind.Api.Steps;
using Nethermind.EthStats.Configs;
using NUnit.Framework;

namespace Nethermind.EthStats.Test;

public class EthStatsPluginTests
{
    [Test]
    public void Enabled_reflects_config([Values] bool enabled)
    {
        EthStatsPlugin plugin = new(new EthStatsConfig { Enabled = enabled });

        Assert.That(plugin.Enabled, Is.EqualTo(enabled));
    }

    [Test]
    public void Module_registers_the_eth_stats_step()
    {
        ContainerBuilder builder = new();
        builder.RegisterModule(new EthStatsPlugin(new EthStatsConfig()).Module);
        using IContainer container = builder.Build();

        Assert.That(container.Resolve<IEnumerable<StepInfo>>().Select(static s => s.StepType),
            Does.Contain(typeof(EthStatsStep)));
    }
}
