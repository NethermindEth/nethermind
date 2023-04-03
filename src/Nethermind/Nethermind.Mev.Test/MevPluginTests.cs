// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Api;
using NSubstitute;
using NUnit.Framework;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Api.Extensions;
using Nethermind.Consensus;

namespace Nethermind.Mev.Test
{
    [TestFixture]
    public class MevPluginTests
    {
        [Test]
        public void Can_create()
        {
            _ = new MevPlugin();
        }

        [Test]
        public void Throws_on_null_api_in_init()
        {
            MevPlugin plugin = new();
            Assert.Throws<ArgumentNullException>(() => plugin.Init(null));
        }

        [Test]
        public void Can_initialize()
        {
            MevPlugin plugin = new();
            plugin.Init(Runner.Test.Ethereum.Build.ContextWithMocks());
            plugin.InitRpcModules();
        }

        [Test]
        public async Task Can_initialize_block_producer()
        {
            // Setup
            MevPlugin plugin = new();
            NethermindApi context = Runner.Test.Ethereum.Build.ContextWithMocks();

            await plugin.Init(context);
            plugin.Enabled.Returns(true);
            await plugin.InitRpcModules();

            IConsensusPlugin consensusPlugin = Substitute.For<IConsensusPlugin>();
            consensusPlugin.InitBlockProducer().Returns(Substitute.For<IBlockProducer>());

            Task<IBlockProducer> blockProducer = plugin.InitBlockProducer(consensusPlugin);

            blockProducer.Result.Should().BeOfType(typeof(MevBlockProducer));
        }
    }
}
