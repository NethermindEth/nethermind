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

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Consumers.Shared;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Subprotocols;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Test.Subprotocols
{
    public class NdmSubprotocolTests
    {
        private ISession _session;
        private INodeStatsManager _nodeStatsManager;
        private IMessageSerializationService _messageSerializationService;
        private IEcdsa _ecdsa;
        private IWallet _wallet;
        private INdmFaucet _faucet;
        private PublicKey _configuredNodeId;
        private IConsumerService _consumerService;
        private INdmConsumerChannelManager _ndmConsumerChannelManager;
        private Address _configuredProviderAddress;
        private Address _configuredConsumerAddress;
        private bool _verifySignature;
        private NdmSubprotocol _subprotocol;

        [SetUp]
        public void Setup()
        {
            _session = Substitute.For<ISession>();
            _nodeStatsManager = Substitute.For<INodeStatsManager>();
            _messageSerializationService = Substitute.For<IMessageSerializationService>();
            _ecdsa = Substitute.For<IEcdsa>();
            _wallet = Substitute.For<IWallet>();
            _faucet = Substitute.For<INdmFaucet>();
            _configuredNodeId = TestItem.PublicKeyA;
            _consumerService = Substitute.For<IConsumerService>();
            _ndmConsumerChannelManager = Substitute.For<INdmConsumerChannelManager>();
            _configuredProviderAddress = TestItem.AddressA;
            _configuredConsumerAddress = TestItem.AddressB;
            _verifySignature = false;
            InitSubprotocol();
        }

        private void InitSubprotocol()
        {
            _subprotocol = new NdmSubprotocol(_session, _nodeStatsManager, _messageSerializationService,
                LimboLogs.Instance, _consumerService, _ndmConsumerChannelManager, _ecdsa, _wallet, _faucet,
                _configuredNodeId, _configuredProviderAddress, _configuredConsumerAddress, _verifySignature);
        }

        [Test]
        public void init_without_signature_verification_should_succeed()
        {
            _subprotocol.Init();
            _wallet.DidNotReceiveWithAnyArgs().Sign(Arg.Any<Keccak>(), Arg.Any<Address>());
        }

        [Test]
        public void init_with_signature_verification_should_succeed()
        {
            _verifySignature = true;
            InitSubprotocol();
            _subprotocol.Init();
            var hash = Keccak.Compute(_configuredNodeId.Address.Bytes);
            _wallet.Received().Sign(hash, _configuredNodeId.Address);
        }

        [Test]
        public void handling_hi_message_should_succeed()
        {
            _verifySignature = true;
            InitSubprotocol();
            var hiMessage = new HiMessage(1, TestItem.AddressC, TestItem.AddressD, TestItem.PublicKeyA,
                new Signature(1, 1, 27));
            var hiPacket = new Packet(hiMessage.Protocol, hiMessage.PacketType,
                _messageSerializationService.Serialize(hiMessage));
            _messageSerializationService.Deserialize<HiMessage>(hiPacket.Data).Returns(hiMessage);
            var hash = Keccak.Compute(hiMessage.NodeId.Bytes);
            _ecdsa.RecoverPublicKey(hiMessage.Signature, hash).Returns(TestItem.PublicKeyA);
            _subprotocol.HandleMessage(hiPacket);
            _ecdsa.Received().RecoverPublicKey(hiMessage.Signature, hash);
            _subprotocol.ProviderAddress.Should().Be(hiMessage.ProviderAddress);
            _subprotocol.ConsumerAddress.Should().Be(hiMessage.ConsumerAddress);
            _consumerService.Received().AddProviderPeer(_subprotocol);
            var getDataAssetsMessage = new GetDataAssetsMessage();
            _messageSerializationService.Serialize(getDataAssetsMessage).Returns(Array.Empty<byte>());
            var getDataAssetsPacket = new Packet(getDataAssetsMessage.Protocol, getDataAssetsMessage.PacketType,
                _messageSerializationService.Serialize(getDataAssetsMessage));
            _messageSerializationService.Deserialize<GetDataAssetsMessage>(getDataAssetsPacket.Data)
                .Returns(getDataAssetsMessage);
            
            Received.InOrder(() =>
            {
                _session.DeliverMessage(Arg.Any<GetDataAssetsMessage>());
                _session.DeliverMessage(Arg.Any<GetDepositApprovalsMessage>());
            });
        }
    }
}