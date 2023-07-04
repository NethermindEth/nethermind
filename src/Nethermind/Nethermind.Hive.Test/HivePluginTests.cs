// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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
            _ = new HivePlugin();
        }

        [Test]
        public void Throws_on_null_api_in_init()
        {
            HivePlugin plugin = new();
            Assert.Throws<ArgumentNullException>(() => plugin.Init(null));
        }

        [Test]
        public void Can_initialize()
        {
            HivePlugin plugin = new();
            plugin.Init(Runner.Test.Ethereum.Build.ContextWithMocks());
            plugin.InitRpcModules();
        }
    }
}
