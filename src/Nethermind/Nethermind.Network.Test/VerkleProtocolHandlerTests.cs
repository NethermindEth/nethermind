// SPDX-FileCopyrightText:2023 Demerzel Solutions Limited
// SPDX-License-Identifier:LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Verkle;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Snap;
using Nethermind.Network.P2P.Subprotocols.Verkle;
using Nethermind.Network.P2P.Subprotocols.Verkle.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Verkle.Tree.Sync;
using NSubstitute;
using NUnit.Framework;


namespace Nethermind.Network.Test;

public class VerkleProtocolHandlerTests
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
                    _messageSerializationService.Register(new SubTreeRangeMessageSerializer());
                }

                return _messageSerializationService;
            }
            set => _messageSerializationService = value;
        }

        public INodeStatsManager NodeStatsManager { get; set; } = Substitute.For<INodeStatsManager>();


        private VerkleProtocolHandler _verkleProtocolHandler;
        public VerkleProtocolHandler VerkleProtocolHandler
        {
            get => _verkleProtocolHandler ??= new VerkleProtocolHandler(
                Session,
                null,
                NodeStatsManager,
                MessageSerializationService,
                LimboLogs.Instance
            );
            set
            {
                _verkleProtocolHandler = value;
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
                        GetSubTreeRangeMessage accountRangeMessage = (GetSubTreeRangeMessage)callInfo[0];
                        _recordedResponseBytesLength.Add(accountRangeMessage.ResponseBytes);

                        if (SimulatedLatency > TimeSpan.Zero)
                        {
                            Task.Delay(SimulatedLatency).Wait();
                        }

                        IByteBuffer buffer = MessageSerializationService.ZeroSerialize(new SubTreeRangeMessage()
                        {
                            PathsWithSubTrees = new[]
                            {
                                new PathWithSubTree(Stem.Zero,
                                    new[]
                                    {
                                        new LeafInSubTree(0, Keccak.EmptyTreeHash.BytesToArray()),
                                        new LeafInSubTree(1, Keccak.EmptyTreeHash.BytesToArray()),
                                        new LeafInSubTree(2, Keccak.EmptyTreeHash.BytesToArray()),
                                        new LeafInSubTree(3, Keccak.EmptyTreeHash.BytesToArray()),
                                        new LeafInSubTree(4, Keccak.EmptyTreeHash.BytesToArray())
                                    })
                            }
                        });
                        buffer.ReadByte(); // Need to skip adaptive type

                        ZeroPacket packet = new(buffer);

                        packet.PacketType = SnapMessageCode.AccountRange;
                        VerkleProtocolHandler.HandleMessage(packet);
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

        VerkleProtocolHandler protocolHandler = ctx.VerkleProtocolHandler;

        ctx.SimulatedLatency = TimeSpan.Zero;
        await protocolHandler.GetSubTreeRange(new SubTreeRange(Keccak.Zero, Stem.Zero), CancellationToken.None);
        await protocolHandler.GetSubTreeRange(new SubTreeRange(Keccak.Zero, Stem.Zero), CancellationToken.None);
        ctx.RecordedMessageSizesShouldIncrease();

        ctx.SimulatedLatency = VerkleProtocolHandler.LowerLatencyThreshold + TimeSpan.FromMilliseconds(1);
        await protocolHandler.GetSubTreeRange(new SubTreeRange(Keccak.Zero, Stem.Zero), CancellationToken.None);
        await protocolHandler.GetSubTreeRange(new SubTreeRange(Keccak.Zero, Stem.Zero), CancellationToken.None);
        ctx.RecordedMessageSizesShouldNotChange();

        ctx.SimulatedLatency = VerkleProtocolHandler.UpperLatencyThreshold + TimeSpan.FromMilliseconds(1);
        await protocolHandler.GetSubTreeRange(new SubTreeRange(Keccak.Zero, Stem.Zero), CancellationToken.None);
        await protocolHandler.GetSubTreeRange(new SubTreeRange(Keccak.Zero, Stem.Zero), CancellationToken.None);
        ctx.RecordedMessageSizesShouldDecrease();
    }

    [Test]
    [Explicit]
    public async Task Test_response_bytes_reset_on_error()
    {
        Context ctx = new Context()
            .WithResponseBytesRecorder;

        VerkleProtocolHandler protocolHandler = ctx.VerkleProtocolHandler;

        // Just setting baseline
        await protocolHandler.GetSubTreeRange(new SubTreeRange(Keccak.Zero, Stem.Zero), CancellationToken.None);
        await protocolHandler.GetSubTreeRange(new SubTreeRange(Keccak.Zero, Stem.Zero), CancellationToken.None);
        ctx.RecordedMessageSizesShouldIncrease();

        ctx.SimulatedLatency = Timeouts.Eth + TimeSpan.FromSeconds(1);
        await protocolHandler.GetSubTreeRange(new SubTreeRange(Keccak.Zero, Stem.Zero), CancellationToken.None);
        ctx.SimulatedLatency = TimeSpan.Zero; // The read value is the request down, but it is adjusted on above request
        await protocolHandler.GetSubTreeRange(new SubTreeRange(Keccak.Zero, Stem.Zero), CancellationToken.None);
        ctx.RecordedMessageSizesShouldDecrease();
    }
}
