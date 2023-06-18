// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using FluentAssertions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Snap;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.State.Snap;
using Nethermind.Stats;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test;

public class SnapProtocolHandlerTests
{
    private class Context
    {
        public ISession Session { get; set; } = Substitute.For<ISession>();

        private IMessageSerializationService _messageSerializationService;
        public IMessageSerializationService MessageSerializationService
        {
            get
            {
                if (_messageSerializationService is null)
                {
                    _messageSerializationService = new MessageSerializationService();
                    _messageSerializationService.Register(new AccountRangeMessageSerializer());
                }

                return _messageSerializationService;
            }
            set => _messageSerializationService = value;
        }

        public INodeStatsManager NodeStatsManager { get; set; } = Substitute.For<INodeStatsManager>();

        private INetworkConfig _networkConfig;
        public INetworkConfig NetworkConfig
        {
            get
            {
                if (_networkConfig is null)
                {
                    _networkConfig = new NetworkConfig();
                }

                return _networkConfig;
            }
            set => _networkConfig = value;
        }


        private SnapProtocolHandler _snapProtocolHandler;
        public SnapProtocolHandler SnapProtocolHandler
        {
            get => _snapProtocolHandler ??= new SnapProtocolHandler(
                Session,
                NodeStatsManager,
                MessageSerializationService,
                NetworkConfig,
                LimboLogs.Instance
            );
            set
            {
                _snapProtocolHandler = value;
            }
        }

        public TimeSpan SimulatedLatency { get; set; } = TimeSpan.Zero;

        private List<long> _recordedResponseBytesLength = new();
        public Context WithResponseBytesRecorder
        {
            get
            {
                Session
                    .When((ses) => ses.DeliverMessage(Arg.Any<P2PMessage>()))
                    .Do((callInfo) =>
                    {
                        GetAccountRangeMessage accountRangeMessage = (GetAccountRangeMessage)callInfo[0];
                        _recordedResponseBytesLength.Add(accountRangeMessage.ResponseBytes);

                        if (SimulatedLatency > TimeSpan.Zero)
                        {
                            Task.Delay(SimulatedLatency).Wait();
                        }

                        IByteBuffer buffer = MessageSerializationService.ZeroSerialize(new AccountRangeMessage()
                        {
                            PathsWithAccounts = new[] { new PathWithAccount(Keccak.Zero, Account.TotallyEmpty) }
                        });
                        buffer.ReadByte(); // Need to skip adaptive type

                        ZeroPacket packet = new(buffer);

                        packet.PacketType = SnapMessageCode.AccountRange;
                        SnapProtocolHandler.HandleMessage(packet);
                        ReferenceCountUtil.Release(packet);
                    });
                return this;
            }
        }

        public void RecordedMessageSizesShouldIncrease()
        {
            _recordedResponseBytesLength[^1].Should().BeGreaterThan(_recordedResponseBytesLength[^2]);
        }

        public void RecordedMessageSizesShouldDecrease()
        {
            _recordedResponseBytesLength[^1].Should().BeLessThan(_recordedResponseBytesLength[^2]);
        }

        public void RecordedMessageSizesShouldNotChange()
        {
            _recordedResponseBytesLength[^1].Should().Be(_recordedResponseBytesLength[^2]);
        }
    }

    [Test]
    public async Task Test_response_bytes_adjust_with_latency()
    {
        Context ctx = new Context()
            .WithResponseBytesRecorder;

        SnapProtocolHandler protocolHandler = ctx.SnapProtocolHandler;

        ctx.SimulatedLatency = TimeSpan.Zero;
        await protocolHandler.GetAccountRange(new AccountRange(Keccak.Zero, Keccak.Zero), CancellationToken.None);
        await protocolHandler.GetAccountRange(new AccountRange(Keccak.Zero, Keccak.Zero), CancellationToken.None);
        ctx.RecordedMessageSizesShouldIncrease();

        ctx.SimulatedLatency = TimeSpan.FromMilliseconds(ctx.NetworkConfig.SnapResponseLatencyLowWatermarkMs) + TimeSpan.FromMilliseconds(1);
        await protocolHandler.GetAccountRange(new AccountRange(Keccak.Zero, Keccak.Zero), CancellationToken.None);
        await protocolHandler.GetAccountRange(new AccountRange(Keccak.Zero, Keccak.Zero), CancellationToken.None);
        ctx.RecordedMessageSizesShouldNotChange();

        ctx.SimulatedLatency = TimeSpan.FromMilliseconds(ctx.NetworkConfig.SnapResponseLatencyHighWatermarkMs) + TimeSpan.FromMilliseconds(1);
        await protocolHandler.GetAccountRange(new AccountRange(Keccak.Zero, Keccak.Zero), CancellationToken.None);
        await protocolHandler.GetAccountRange(new AccountRange(Keccak.Zero, Keccak.Zero), CancellationToken.None);
        ctx.RecordedMessageSizesShouldDecrease();
    }

    [Test]
    [Explicit]
    public async Task Test_response_bytes_reset_on_error()
    {
        Context ctx = new Context()
            .WithResponseBytesRecorder;

        SnapProtocolHandler protocolHandler = ctx.SnapProtocolHandler;

        // Just setting baseline
        await protocolHandler.GetAccountRange(new AccountRange(Keccak.Zero, Keccak.Zero), CancellationToken.None);
        await protocolHandler.GetAccountRange(new AccountRange(Keccak.Zero, Keccak.Zero), CancellationToken.None);
        ctx.RecordedMessageSizesShouldIncrease();

        ctx.SimulatedLatency = Timeouts.Eth + TimeSpan.FromSeconds(1);
        await protocolHandler.GetAccountRange(new AccountRange(Keccak.Zero, Keccak.Zero), CancellationToken.None);
        ctx.SimulatedLatency = TimeSpan.Zero; // The read value is the request down, but it is adjusted on above request
        await protocolHandler.GetAccountRange(new AccountRange(Keccak.Zero, Keccak.Zero), CancellationToken.None);
        ctx.RecordedMessageSizesShouldDecrease();
    }
}
