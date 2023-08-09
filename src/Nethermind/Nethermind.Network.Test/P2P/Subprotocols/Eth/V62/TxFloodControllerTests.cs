// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Network.Config;
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
        private ITimestamper _timestamper;

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
                Substitute.For<IGossipPolicy>(),
            Substitute.For<INetworkConfig>(),
                LimboLogs.Instance);

            _timestamper = Substitute.For<ITimestamper>();
            _timestamper.UtcNow.Returns(c => DateTime.UtcNow);
            _controller = new TxFloodController(_handler, _timestamper, LimboNoErrorLogger.Instance);
        }

        [Test]
        public void Is_allowed_will_be_true_unless_misbehaving()
        {
            for (int i = 0; i < 10000; i++)
            {
                _controller.IsAllowed().Should().BeTrue();
            }
        }

        [Test]
        public void Is_allowed_will_be_false_when_misbehaving()
        {
            for (int i = 0; i < 601; i++)
            {
                _controller.Report(false);
            }

            int allowedCount = 0;
            for (int i = 0; i < 10000; i++)
            {
                if (_controller.IsAllowed()) allowedCount++;
            }

            allowedCount.Should().BeInRange(500, 1500);
        }

        [Test]
        public void Will_only_get_disconnected_when_really_flooding()
        {
            for (int i = 0; i < 600; i++)
            {
                _controller.Report(false);
            }

            // for easier debugging
            _controller.Report(false);

            _session.DidNotReceiveWithAnyArgs()
                .InitiateDisconnect(DisconnectReason.TxFlooding, null);

            for (int i = 0; i < 6000 - 601; i++)
            {
                _controller.Report(false);
            }

            // for easier debugging
            _controller.Report(false);

            _session.Received()
                .InitiateDisconnect(DisconnectReason.TxFlooding, Arg.Any<string>());
        }

        [Test]
        public void Will_downgrade_at_first()
        {
            for (int i = 0; i < 1000; i++)
            {
                _controller.Report(false);
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

        [Test]
        public void Misbehaving_expires()
        {
            for (int i = 0; i < 1000; i++)
            {
                _controller.Report(false);
            }

            _controller.IsDowngraded.Should().BeTrue();
            _timestamper.UtcNow.Returns(DateTime.UtcNow.AddSeconds(61));
            _controller.Report(false);
            _controller.IsDowngraded.Should().BeFalse();
        }
    }
}
