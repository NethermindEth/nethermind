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
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V62
{
    [TestFixture]
    public class TxFloodControllerTests
    {
        private TxFloodController _controller;
        private Eth62ProtocolHandler _handler;
        private ISession _session;

        [SetUp]
        public void Setup()
        {
            _session = Substitute.For<ISession>();
            _handler = new Eth62ProtocolHandler(
                _session,
                Substitute.For<IMessageSerializationService>(),
                Substitute.For<INodeStatsManager>(),
                Substitute.For<ISyncServer>(),
                Substitute.For<ITxPool>(),
                LimboLogs.Instance);

            _controller = new TxFloodController(_handler, LimboNoErrorLogger.Instance);
        }

        [Test]
        public void Is_allowed_will_be_true_unless_misbehaving()
        {
            int allowedCount = 0;
            for (int i = 0; i < 10000; i++)
            {
                if (_controller.IsAllowed()) allowedCount++;
            }

            allowedCount.Should().BeGreaterThan(500).And.BeLessThan(1500);
        }
        
        [Test]
        public void Is_allowed_will_be_false_when_misbehaving()
        {
            for (int i = 0; i < 601; i++)
            {
                _controller.ReportNotAccepted();
            }
            
            for (int i = 0; i < 10000; i++)
            {
                _controller.IsAllowed().Should().BeFalse();
            }
        }
        
        [Test]
        public void Will_only_get_disconnected_when_really_flooding()
        {
            for (int i = 0; i < 600; i++)
            {
                _controller.ReportNotAccepted();
            }
            
            // for easier debugging
            _controller.ReportNotAccepted();
            
            _session.DidNotReceiveWithAnyArgs()
                .InitiateDisconnect(DisconnectReason.UselessPeer, null);
            
            for (int i = 0; i < 6000 - 601; i++)
            {
                _controller.ReportNotAccepted();
            }
            
            // for easier debugging
            _controller.ReportNotAccepted();
            
            _session.Received()
                .InitiateDisconnect(DisconnectReason.UselessPeer, Arg.Any<string>());
        }
        
        [Test]
        public void Will_downgrade_at_first()
        {
            for (int i = 0; i < 1000; i++)
            {
                _controller.ReportNotAccepted();
            }

            _controller.IsDowngraded.Should().BeTrue();
        }
        
        [Test]
        public void Enabled_by_default()
        {
            _controller.IsEnabled.Should().BeTrue();
        }
        
        [Test]
        public void Can_be_disabled_and_enabled()
        {
            _controller.IsEnabled = false;
            _controller.IsEnabled.Should().BeFalse();
            _controller.IsEnabled = false;
            _controller.IsEnabled.Should().BeFalse();
            _controller.IsEnabled = true;
            _controller.IsEnabled.Should().BeTrue();
            _controller.IsEnabled = true;
            _controller.IsEnabled.Should().BeTrue();
        }
    }
}