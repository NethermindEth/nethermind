// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using DotNetty.Handlers.Logging;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Discv4.Kademlia;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Network.Enr;
using Nethermind.Network.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv4
{
    [Parallelizable(ParallelScope.None)] // Some test check for global metric
    [TestFixture]
    public class NettyDiscoveryHandlerTests
    {
        private readonly PrivateKey _privateKey = new("49a7b37aa6f6645917e7b807e9d1c00d4fa71f18343b0d4122a4d2df64dd6fee");
        private readonly PrivateKey _privateKey2 = new("3a1076bf45ab87712ad64ccb3b10217737f7faacbf2872e88fdd9a537d8fe266");
        private List<IChannel> _channels = [];
        private List<NettyDiscoveryHandler> _discoveryHandlers = [];
        private List<IKademliaAdapter> _kademliaAdaptersMocks = [];
        private readonly IPEndPoint _address = new(IPAddress.Loopback, 10001);
        private readonly IPEndPoint _address2 = new(IPAddress.Loopback, 10002);
        private int _channelActivatedCounter;
        private IChannelFactory _channelFactory = new LocalChannelFactory(nameof(NettyDiscoveryBaseHandler), new NetworkConfig());

        [SetUp]
        public async Task Initialize()
        {
            _channels = [];
            _discoveryHandlers = [];
            _kademliaAdaptersMocks = [];
            _channelActivatedCounter = 0;
            IKademliaAdapter? kademliaAdapterMock = Substitute.For<IKademliaAdapter>();
            kademliaAdapterMock.OnIncomingMsg(Arg.Any<DiscoveryMsg>()).Returns(Task.CompletedTask);
            IMessageSerializationService? messageSerializationService = Build.A.SerializationService().WithDiscovery(_privateKey).TestObject;

            IKademliaAdapter? kademliaAdapterMock2 = Substitute.For<IKademliaAdapter>();
            kademliaAdapterMock2.OnIncomingMsg(Arg.Any<DiscoveryMsg>()).Returns(Task.CompletedTask);
            IMessageSerializationService? messageSerializationService2 = Build.A.SerializationService().WithDiscovery(_privateKey).TestObject;

            await StartUdpChannel("127.0.0.1", 10001, kademliaAdapterMock, messageSerializationService);
            await StartUdpChannel("127.0.0.1", 10002, kademliaAdapterMock2, messageSerializationService2);

            _kademliaAdaptersMocks.Add(kademliaAdapterMock);
            _kademliaAdaptersMocks.Add(kademliaAdapterMock2);

            Assert.That(() => _channelActivatedCounter, Is.EqualTo(2).After(1000, 100));
        }

        [TearDown]
        public async Task CleanUp()
        {
            _channels.ForEach(static x => { x.CloseAsync(); });
            await Task.Delay(50);
        }

        [Test]
        public async Task PingSentReceivedTest()
        {
            PingMsg msg = new(_privateKey2.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, _address, _address2, new byte[32])
            {
                FarAddress = _address2
            };

            await _discoveryHandlers[0].SendMsg(msg);
            await SleepWhileWaiting();
            await _kademliaAdaptersMocks[1].Received(1).OnIncomingMsg(Arg.Is<DiscoveryMsg>(static x => x.MsgType == MsgType.Ping));

            PingMsg msg2 = new(_privateKey.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, _address2, _address, new byte[32])
            {
                FarAddress = _address
            };

            await _discoveryHandlers[1].SendMsg(msg2);
            await SleepWhileWaiting();
            await _kademliaAdaptersMocks[0].Received(1).OnIncomingMsg(Arg.Is<DiscoveryMsg>(static x => x.MsgType == MsgType.Ping));
        }

        [Test]
        public async Task PongSentReceivedTest()
        {
            PongMsg msg = new(_privateKey2.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, TestItem.KeccakA.ValueHash256)
            {
                FarAddress = _address2
            };

            await _discoveryHandlers[0].SendMsg(msg);
            await SleepWhileWaiting();
            await _kademliaAdaptersMocks[1].Received(1).OnIncomingMsg(Arg.Is<DiscoveryMsg>(static x => x.MsgType == MsgType.Pong));

            PongMsg msg2 = new(_privateKey.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, TestItem.KeccakA.ValueHash256)
            {
                FarAddress = _address
            };
            await _discoveryHandlers[1].SendMsg(msg2);
            await SleepWhileWaiting();
            await _kademliaAdaptersMocks[0].Received(1).OnIncomingMsg(Arg.Is<DiscoveryMsg>(static x => x.MsgType == MsgType.Pong));
        }

        [Test]
        public async Task FindNodeSentReceivedTest()
        {
            FindNodeMsg msg = new(_privateKey2.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, new byte[] { 1, 2, 3 })
            {
                FarAddress = _address2
            };

            await _discoveryHandlers[0].SendMsg(msg);
            await SleepWhileWaiting();
            await _kademliaAdaptersMocks[1].Received(1).OnIncomingMsg(Arg.Is<DiscoveryMsg>(static x => x.MsgType == MsgType.FindNode));

            FindNodeMsg msg2 = new(_privateKey2.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, new byte[] { 1, 2, 3 })
            {
                FarAddress = _address
            };

            await _discoveryHandlers[1].SendMsg(msg2);
            await SleepWhileWaiting();
            await _kademliaAdaptersMocks[0].Received(1).OnIncomingMsg(Arg.Is<DiscoveryMsg>(static x => x.MsgType == MsgType.FindNode));
        }

        [Test]
        public async Task NeighborsSentReceivedTest()
        {
            NeighborsMsg msg = new(_privateKey2.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, Array.Empty<Node>())
            {
                FarAddress = _address2
            };

            await _discoveryHandlers[0].SendMsg(msg);
            await SleepWhileWaiting();
            await _kademliaAdaptersMocks[1].Received(1).OnIncomingMsg(Arg.Is<DiscoveryMsg>(static x => x.MsgType == MsgType.Neighbors));

            NeighborsMsg msg2 = new(_privateKey.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, Array.Empty<Node>())
            {
                FarAddress = _address,
            };

            await _discoveryHandlers[1].SendMsg(msg2);
            await SleepWhileWaiting();
            await _kademliaAdaptersMocks[0].Received(1).OnIncomingMsg(Arg.Is<DiscoveryMsg>(static x => x.MsgType == MsgType.Neighbors));
        }

        private (IKademliaAdapter Adapter, NettyDiscoveryHandler Handler, IChannelHandlerContext Ctx, IMessageSerializationService Service) CreateHandler(
            NodeFilter? nodeFilter = null,
            int? globalInboundMessageBurst = null,
            int? inboundMessageQueueCapacity = null,
            int? inboundMessageWorkerCount = null,
            IMessageSerializationService? messageSerializationService = null)
        {
            IKademliaAdapter adapter = Substitute.For<IKademliaAdapter>();
            adapter.OnIncomingMsg(Arg.Any<DiscoveryMsg>()).Returns(Task.CompletedTask);
            IMessageSerializationService service = messageSerializationService ?? Build.A.SerializationService().WithDiscovery(_privateKey2).TestObject;
            IChannel channel = Substitute.For<IChannel>();
            NettyDiscoveryHandler handler = new(
                adapter,
                channel,
                service,
                Timestamper.Default,
                LimboLogs.Instance,
                nodeFilter,
                globalInboundMessageBurst,
                inboundMessageQueueCapacity,
                inboundMessageWorkerCount);
            IChannelHandlerContext ctx = Substitute.For<IChannelHandlerContext>();
            return (adapter, handler, ctx, service);
        }

        [Test]
        public void UndersizedPacketIsNotForwardedToDiscoveryManager()
        {
            (IKademliaAdapter adapter, NettyDiscoveryHandler handler, IChannelHandlerContext ctx, IMessageSerializationService _) = CreateHandler();

            byte[] data = new byte[50];
            IPEndPoint from = IPEndPoint.Parse("127.0.0.1:10000");
            IPEndPoint to = IPEndPoint.Parse("127.0.0.1:10003");
            handler.ChannelRead(ctx, new DatagramPacket(Unpooled.WrappedBuffer(data), from, to));

            _ = adapter.DidNotReceive().OnIncomingMsg(Arg.Any<DiscoveryMsg>());
        }

        [Test]
        public void ForwardsUnrecognizedMessageToNextHandler()
        {
            byte[] data = [1, 2, 3];
            IPEndPoint from = IPEndPoint.Parse("127.0.0.1:10000");
            IPEndPoint to = IPEndPoint.Parse("127.0.0.1:10003");
            DatagramPacket packet = new(Unpooled.WrappedBuffer(data), from, to);

            IChannelHandlerContext ctx = Substitute.For<IChannelHandlerContext>();
            _discoveryHandlers[0].ChannelRead(ctx, packet);

            ctx.FireChannelRead(Arg.Is<DatagramPacket>(
                p => p.Content.ReadAllBytesAsArray().SequenceEqual(data)
            ));
        }

        [Test]
        public async Task FarFutureMessagesAreRejected()
        {
            PingMsg msg = new(_privateKey2.PublicKey, Timestamper.Default.UnixTime.SecondsLong + (long)TimeSpan.FromHours(2).TotalSeconds, _address, _address2, new byte[32])
            {
                FarAddress = _address2
            };

            await _discoveryHandlers[0].SendMsg(msg);
            await SleepWhileWaiting();

            _ = _kademliaAdaptersMocks[1].DidNotReceive().OnIncomingMsg(Arg.Any<DiscoveryMsg>());
        }

        [Test]
        public async Task EnrResponseWithoutExpirationIsAccepted()
        {
            (IKademliaAdapter adapter, NettyDiscoveryHandler handler, IChannelHandlerContext ctx, IMessageSerializationService service) = CreateHandler();

            EnrResponseMsg msg = BuildEnrResponse(_privateKey2);
            IByteBuffer serialized = service.ZeroSerialize(msg);
            byte[] data;
            try
            {
                data = serialized.ReadAllBytesAsArray();
            }
            finally
            {
                serialized.SafeRelease();
            }

            handler.ChannelRead(ctx, new DatagramPacket(Unpooled.WrappedBuffer(data), _address2, _address));

            await SleepWhileWaiting();

            await adapter.Received(1).OnIncomingMsg(Arg.Is<DiscoveryMsg>(static m => m.MsgType == MsgType.EnrResponse));
            ctx.DidNotReceive().FireChannelRead(Arg.Any<object>());
        }

        [Test]
        public async Task RateLimitedMessagesAreIgnored()
        {
            (IKademliaAdapter adapter, NettyDiscoveryHandler handler, IChannelHandlerContext ctx, IMessageSerializationService service) = CreateHandler(NodeFilter.CreateExact(16, TimeSpan.FromMinutes(1)));
            using SemaphoreSlim called = new(0);
            adapter.When(x => x.OnIncomingMsg(Arg.Any<DiscoveryMsg>())).Do(_ => called.Release());

            byte[] data = SerializePing(service);

            handler.ChannelRead(ctx, new DatagramPacket(Unpooled.WrappedBuffer((byte[])data.Clone()), _address2, _address));
            handler.ChannelRead(ctx, new DatagramPacket(Unpooled.WrappedBuffer((byte[])data.Clone()), _address2, _address));

            Assert.That(await called.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
            await Task.Delay(50);

            await adapter.Received(1).OnIncomingMsg(Arg.Any<DiscoveryMsg>());
            ctx.DidNotReceive().FireChannelRead(Arg.Any<object>());
        }

        [Test]
        public async Task DefaultInboundRateLimiter_Allows_ShortBurstFromSameIp()
        {
            (IKademliaAdapter adapter, NettyDiscoveryHandler handler, IChannelHandlerContext ctx, IMessageSerializationService service) = CreateHandler();

            byte[] data = SerializePing(service);

            handler.ChannelRead(ctx, new DatagramPacket(Unpooled.WrappedBuffer((byte[])data.Clone()), _address2, _address));
            handler.ChannelRead(ctx, new DatagramPacket(Unpooled.WrappedBuffer((byte[])data.Clone()), _address2, _address));

            await SleepWhileWaiting();

            await adapter.Received(2).OnIncomingMsg(Arg.Any<DiscoveryMsg>());
            ctx.DidNotReceive().FireChannelRead(Arg.Any<object>());
        }

        [Test]
        public async Task DefaultInboundRateLimiter_Drops_Message_AboveBurstLimit()
        {
            (IKademliaAdapter adapter, NettyDiscoveryHandler handler, IChannelHandlerContext ctx, IMessageSerializationService service) = CreateHandler();

            byte[] data = SerializePing(service);

            for (int i = 0; i < 9; i++)
            {
                handler.ChannelRead(ctx, new DatagramPacket(Unpooled.WrappedBuffer((byte[])data.Clone()), _address2, _address));
            }

            await SleepWhileWaiting();

            await adapter.Received(8).OnIncomingMsg(Arg.Any<DiscoveryMsg>());
        }

        [Test]
        public async Task DualStackMappedSender_IsAcceptedAndNormalizedToIPv4()
        {
            (IKademliaAdapter adapter, NettyDiscoveryHandler handler, IChannelHandlerContext ctx, IMessageSerializationService service) = CreateHandler();

            DiscoveryMsg? received = null;
            _ = adapter.OnIncomingMsg(Arg.Do<DiscoveryMsg>(x => received = x));

            IPEndPoint mappedSender = new(IPAddress.Parse("::ffff:127.0.0.2"), _address2.Port);
            byte[] data = SerializePing(service);

            handler.ChannelRead(ctx, new DatagramPacket(Unpooled.WrappedBuffer(data), mappedSender, _address));

            await SleepWhileWaiting();

            await adapter.Received(1).OnIncomingMsg(Arg.Any<DiscoveryMsg>());
            ctx.DidNotReceive().FireChannelRead(Arg.Any<object>());
            Assert.That(received?.FarAddress?.Address, Is.EqualTo(IPAddress.Parse("127.0.0.2")));
        }

        [Test]
        public async Task GlobalInboundRateLimiter_Drops_Messages_AboveBurstLimit()
        {
            (IKademliaAdapter adapter, NettyDiscoveryHandler handler, IChannelHandlerContext ctx, IMessageSerializationService service) = CreateHandler(globalInboundMessageBurst: 2);
            using SemaphoreSlim called = new(0);
            adapter.When(x => x.OnIncomingMsg(Arg.Any<DiscoveryMsg>())).Do(_ => called.Release());

            byte[] data = SerializePing(service);

            for (int i = 0; i < 3; i++)
            {
                IPEndPoint sender = new(IPAddress.Parse($"127.0.1.{i + 1}"), _address2.Port);
                handler.ChannelRead(ctx, new DatagramPacket(Unpooled.WrappedBuffer((byte[])data.Clone()), sender, _address));
            }

            Assert.That(await called.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(await called.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
            await Task.Delay(50);

            await adapter.Received(2).OnIncomingMsg(Arg.Any<DiscoveryMsg>());
        }

        [Test]
        public async Task InboundDispatchQueue_Drops_Messages_WhenFull()
        {
            IMessageSerializationService innerService = Build.A.SerializationService().WithDiscovery(_privateKey2).TestObject;
            using ManualResetEventSlim deserializeEntered = new();
            using ManualResetEventSlim unblockDeserialize = new();
            BlockingSerializationService blockingService = new(innerService, deserializeEntered, unblockDeserialize);
            (IKademliaAdapter adapter, NettyDiscoveryHandler handler, IChannelHandlerContext ctx, IMessageSerializationService service) = CreateHandler(
                globalInboundMessageBurst: 64,
                inboundMessageQueueCapacity: 1,
                inboundMessageWorkerCount: 1,
                messageSerializationService: blockingService);
            int received = 0;
            adapter.OnIncomingMsg(Arg.Any<DiscoveryMsg>()).Returns(_ =>
            {
                Interlocked.Increment(ref received);
                return Task.CompletedTask;
            });

            byte[] data = SerializePing(service);

            handler.ChannelRead(ctx, new DatagramPacket(Unpooled.WrappedBuffer((byte[])data.Clone()), new IPEndPoint(IPAddress.Parse("127.0.2.1"), _address2.Port), _address));
            Assert.That(deserializeEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);

            for (int i = 1; i < 16; i++)
            {
                IPEndPoint sender = new(IPAddress.Parse($"127.0.2.{i + 1}"), _address2.Port);
                handler.ChannelRead(ctx, new DatagramPacket(Unpooled.WrappedBuffer((byte[])data.Clone()), sender, _address));
            }

            unblockDeserialize.Set();

            Assert.That(() => Interlocked.CompareExchange(ref received, 0, 0), Is.EqualTo(2).After(5000, 10));
        }

        private byte[] SerializePing(IMessageSerializationService service)
        {
            PingMsg msg = new(_privateKey2.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, _address2, _address, new byte[32])
            {
                FarAddress = _address
            };

            IByteBuffer serialized = service.ZeroSerialize(msg);
            byte[] data = serialized.ReadAllBytesAsArray();
            serialized.SafeRelease();
            return data;
        }

        private EnrResponseMsg BuildEnrResponse(PrivateKey signingKey)
        {
            NodeRecord nodeRecord = new();
            nodeRecord.SetEntry(new SecP256k1Entry(signingKey.CompressedPublicKey));
            nodeRecord.EnrSequence = 5;
            NodeRecordSigner signer = new(new Ecdsa(), signingKey);
            signer.Sign(nodeRecord);
            return new EnrResponseMsg(_address, nodeRecord, TestItem.KeccakA);
        }

        private async Task StartUdpChannel(string address, int port, IKademliaAdapter kademliaAdapter, IMessageSerializationService service)
        {
            MultithreadEventLoopGroup group = new(1);

            Bootstrap bootstrap = new();
            bootstrap
                .Group(group)
                .ChannelFactory(() => _channelFactory.CreateDatagramChannel())
                .Handler(new ActionChannelInitializer<IDatagramChannel>(x => InitializeChannel(x, kademliaAdapter, service)));

            _channels.Add(await bootstrap.BindAsync(IPAddress.Parse(address), port));
        }

        private void InitializeChannel(IDatagramChannel channel, IKademliaAdapter kademliaAdapter, IMessageSerializationService service)
        {
            NettyDiscoveryHandler handler = new(kademliaAdapter, channel, service, new Timestamper(), LimboLogs.Instance);
            handler.OnChannelActivated += (_, _) =>
            {
                _channelActivatedCounter++;
            };
            _discoveryHandlers.Add(handler);
            kademliaAdapter.MsgSender = handler;
            channel.Pipeline
                .AddLast(new LoggingHandler(DotNetty.Handlers.Logging.LogLevel.TRACE))
                .AddLast(handler);
        }

        private static async Task SleepWhileWaiting() =>
            await Task.Delay((TestContext.CurrentContext.CurrentRepeatCount + 1) * 300);

        private sealed class BlockingSerializationService(
            IMessageSerializationService innerService,
            ManualResetEventSlim deserializeEntered,
            ManualResetEventSlim unblockDeserialize) : IMessageSerializationService
        {
            private int _deserializeCalls;

            public IByteBuffer ZeroSerialize<T>(T message, IByteBufferAllocator? allocator = null) where T : MessageBase
                => innerService.ZeroSerialize(message, allocator);

            public T Deserialize<T>(ArraySegment<byte> bytes) where T : MessageBase
            {
                if (typeof(T) == typeof(PingMsg) && Interlocked.Increment(ref _deserializeCalls) == 1)
                {
                    deserializeEntered.Set();
                    unblockDeserialize.Wait(TimeSpan.FromSeconds(10));
                }

                return innerService.Deserialize<T>(bytes);
            }

            public T Deserialize<T>(IByteBuffer buffer) where T : MessageBase
                => innerService.Deserialize<T>(buffer);
        }
    }
}
