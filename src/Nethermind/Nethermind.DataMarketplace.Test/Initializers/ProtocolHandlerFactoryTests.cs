// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Transport.Channels;
using FluentAssertions;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Initializers;
using Nethermind.DataMarketplace.Subprotocols;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;
using Session = Nethermind.Network.P2P.Session;

namespace Nethermind.DataMarketplace.Test.Initializers
{
    public class ProtocolHandlerFactoryTests
    {
        private INdmSubprotocolFactory _subprotocolFactory;
        private IProtocolValidator _protocolValidator;
        private IEthRequestService _ethRequestService;
        private IProtocolHandlerFactory _factory;

        [SetUp]
        public void Setup()
        {
            _subprotocolFactory = Substitute.For<INdmSubprotocolFactory>();
            _protocolValidator = Substitute.For<IProtocolValidator>();
            _ethRequestService = Substitute.For<IEthRequestService>();
            _factory = new ProtocolHandlerFactory(_subprotocolFactory, _protocolValidator, _ethRequestService,
                LimboLogs.Instance);
        }

        [Test]
        public void create_should_return_protocol_handler()
        {
            var protocolHandler = Substitute.For<IProtocolHandler>();
            var session = Substitute.For<ISession>();
            _subprotocolFactory.Create(session).Returns(protocolHandler);
            var handler = _factory.Create(session);
            _subprotocolFactory.Received().Create(session);
            handler.Should().Be(protocolHandler);
        }

        [Test]
        public void protocol_initialized_event_should_be_handled()
        {
            var protocolHandler = Substitute.For<IProtocolHandler>();
            var session = Substitute.For<ISession>();
            var node = new Node("127.0.0.1", 8545);
            session.Node.Returns(node);
            _subprotocolFactory.Create(session).Returns(protocolHandler);
            _factory.Create(session);
            var eventArgs = new NdmProtocolInitializedEventArgs(protocolHandler);
            protocolHandler.ProtocolInitialized += Raise.EventWith(protocolHandler,
                (ProtocolInitializedEventArgs)eventArgs);
            _subprotocolFactory.Received().Create(session);
            _protocolValidator.Received().DisconnectOnInvalid(Protocol.Ndm, session, eventArgs);
            _ethRequestService.DidNotReceiveWithAnyArgs().UpdateFaucet(Arg.Any<INdmPeer>());
        }

        [Test]
        public void protocol_initialized_event_should_be_and_set_to_faucet_if_host_address_doest_match()
        {
            var protocolHandler = Substitute.For<IProtocolHandler, INdmPeer>();
            const string host = "127.0.0.1";
            var node = new Node(host, 8545);
            var session = new Session(8545, node, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            _ethRequestService.FaucetHost.Returns(host);
            _subprotocolFactory.Create(session).Returns(protocolHandler);
            _factory.Create(session);
            var eventArgs = new NdmProtocolInitializedEventArgs(protocolHandler);
            protocolHandler.ProtocolInitialized += Raise.EventWith(protocolHandler,
                (ProtocolInitializedEventArgs)eventArgs);
            _subprotocolFactory.Received().Create(session);
            _protocolValidator.Received().DisconnectOnInvalid(Protocol.Ndm, session, eventArgs);
            _ethRequestService.Received().UpdateFaucet(protocolHandler as INdmPeer);
        }
    }
}
