//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Numerics;
using Nethermind.Api;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using FluentAssertions;
using Nethermind.Api.Extensions;
using Nethermind.Consensus;
using Nethermind.Core.Test.Builders;
using Nethermind.Mev.Data;
using Nethermind.Mev.Source;
using Nethermind.Runner.Ethereum.Api;

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
