// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Api.Extensions;
using Nethermind.Core;
using Nethermind.Core.Test.Modules;
using NUnit.Framework;

namespace Nethermind.Hive.Tests
{
    public class HivePluginTests
    {
        [Test]
        public void Can_create()
        {
            _ = new HivePlugin(new HiveConfig() { Enabled = true });
        }

        [Test]
        public void Can_initialize()
        {
            INethermindPlugin plugin = new HivePlugin(new HiveConfig() { Enabled = true });
            plugin.Init(Runner.Test.Ethereum.Build.ContextWithMocks());
            plugin.InitRpcModules();
        }

        [Test]
        public void Can_resolve_hive_step()
        {
            using IContainer container = new ContainerBuilder()
                .AddModule(new TestNethermindModule())
                .AddModule(new HiveModule())
                .Build();

            container.Resolve<HiveStep>();
        }
    }
}
