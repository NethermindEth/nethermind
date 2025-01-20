// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
        public void Throws_on_null_api_in_init()
        {
            HivePlugin plugin = new(new HiveConfig() { Enabled = true });
            Assert.Throws<ArgumentNullException>(() => plugin.Init(null));
        }

        [Test]
        public void Can_initialize()
        {
            HivePlugin plugin = new(new HiveConfig() { Enabled = true });
            plugin.Init(Runner.Test.Ethereum.Build.ContextWithMocks());
            plugin.InitRpcModules();
        }
    }
}
