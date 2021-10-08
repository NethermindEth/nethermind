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

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Consumers.Shared;
using Nethermind.DataMarketplace.Core.Events;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Subprotocols;
using Nethermind.DataMarketplace.Subprotocols.Factories;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Stats;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Test.Subprotocols
{
    public class NdmSubprotocolFactoryTests
    {
        private IMessageSerializationService _messageSerializationService;
        private INodeStatsManager _nodeStatsManager;
        private ILogManager _logManager;
        private IAccountService _accountService;
        private IConsumerService _consumerService;
        private INdmConsumerChannelManager _ndmConsumerChannelManager;
        private IEcdsa _ecdsa;
        private IWallet _wallet;
        private INdmFaucet _faucet;
        private PublicKey _nodeId;
        private Address _providerAddress;
        private Address _consumerAddress;
        private bool _verifySignature;
        private INdmSubprotocolFactory _factory;

        [SetUp]
        public void Setup()
        {
            _messageSerializationService = Substitute.For<IMessageSerializationService>();
            _nodeStatsManager = Substitute.For<INodeStatsManager>();
            _logManager = LimboLogs.Instance;
            _accountService = Substitute.For<IAccountService>();
            _consumerService = Substitute.For<IConsumerService>();
            _ndmConsumerChannelManager = Substitute.For<INdmConsumerChannelManager>();
            _ecdsa = Substitute.For<IEcdsa>();
            _wallet = Substitute.For<IWallet>();
            _faucet = Substitute.For<INdmFaucet>();
            _nodeId = TestItem.PublicKeyA;
            _providerAddress = TestItem.AddressA;
            _consumerAddress = TestItem.AddressB;
            _verifySignature = false;
            _factory = new NdmSubprotocolFactory(_messageSerializationService, _nodeStatsManager,
                _logManager, _accountService, _consumerService, _ndmConsumerChannelManager, _ecdsa, _wallet, _faucet,
                _nodeId, _providerAddress, _consumerAddress, _verifySignature);
        }

        [Test]
        public void given_valid_session_ndm_subprotocol_should_be_created()
        {
            var newConsumerAddress = TestItem.AddressC;
            var session = Substitute.For<ISession>();
            var eventArgs = new AddressChangedEventArgs(_consumerAddress, newConsumerAddress);
            _accountService.AddressChanged += Raise.EventWith(_consumerService, eventArgs);
            var subprotocol = _factory.Create(session);
            subprotocol.Should().NotBeNull();
        }
    }
}