//  Copyright (c) 2018 Demerzel Solutions Limited
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

using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Consumers.Shared;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Specs;
using Nethermind.Stats;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Subprotocols.Test
{
    [TestFixture]
    public class NdmSubprotocolTests
    {
        private NdmSubprotocol _subprotocol;

        [SetUp]
        public void Setup()
        {
            ISession session = Substitute.For<ISession>();
            INodeStatsManager nodeStatsManager = new NodeStatsManager(new StatsConfig(), LimboLogs.Instance);
            MessageSerializationService serializationService = new MessageSerializationService();
            serializationService.Register(typeof(HiMessage).Assembly);
            IConsumerService consumerService = Substitute.For<IConsumerService>();
            INdmConsumerChannelManager consumerChannelManager = Substitute.For<INdmConsumerChannelManager>();
            EthereumEcdsa ecdsa = new EthereumEcdsa(MainNetSpecProvider.Instance, LimboLogs.Instance);
            _subprotocol = new NdmSubprotocol(session, nodeStatsManager, serializationService, LimboLogs.Instance, consumerService, consumerChannelManager, ecdsa, new DevWallet(new WalletConfig(), LimboLogs.Instance), Substitute.For<INdmFaucet>(), TestItem.PublicKeyB, TestItem.AddressB, TestItem.AddressA, false);
        }
    }
}