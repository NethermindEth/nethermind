// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using Nethermind.Core;
using Nethermind.Core.Test;
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
    public class DiscoveryHandlerTests
    {
        private readonly PrivateKey _privateKey = new("49a7b37aa6f6645917e7b807e9d1c00d4fa71f18343b0d4122a4d2df64dd6fee");
        private readonly PrivateKey _privateKey2 = new("3a1076bf45ab87712ad64ccb3b10217737f7faacbf2872e88fdd9a537d8fe266");
        private List<DiscoveryConnectionsPool> _pools = [];
        private List<DiscoveryHandler> _discoveryHandlers = [];
        private List<IKademliaAdapter> _kademliaAdaptersMocks = [];
        private readonly IPEndPoint _address = new(IPAddress.Loopback, 10001);
        private readonly IPEndPoint _address2 = new(IPAddress.Loopback, 10002);
        private IChannelFactory _channelFactory = new LocalChannelFactory(nameof(DiscoveryHandlerTests), new NetworkConfig());

        [SetUp]
        public void Initialize()
        {
            _pools = [];
            _discoveryHandlers = [];
            _kademliaAdaptersMocks = [];
            IKademliaAdapter? kademliaAdapterMock = Substitute.For<IKademliaAdapter>();
            kademliaAdapterMock.OnIncomingMsg(Arg.Any<DiscoveryMsg>()).Returns(Task.CompletedTask);
            IMessageSerializationService? messageSerializationService = Build.A.SerializationService().WithDiscovery(_privateKey).TestObject;

            IKademliaAdapter? kademliaAdapterMock2 = Substitute.For<IKademliaAdapter>();
            kademliaAdapterMock2.OnIncomingMsg(Arg.Any<DiscoveryMsg>()).Returns(Task.CompletedTask);
            IMessageSerializationService? messageSerializationService2 = Build.A.SerializationService().WithDiscovery(_privateKey).TestObject;

            StartUdpChannel("127.0.0.1", 10001, kademliaAdapterMock, messageSerializationService);
            StartUdpChannel("127.0.0.1", 10002, kademliaAdapterMock2, messageSerializationService2);

            _kademliaAdaptersMocks.Add(kademliaAdapterMock);
            _kademliaAdaptersMocks.Add(kademliaAdapterMock2);
        }

        [TearDown]
        public async Task CleanUp()
        {
            foreach (DiscoveryConnectionsPool pool in _pools)
            {
                await pool.StopAsync();
            }
        }

        [Test]
        public async Task Send_releases_serialized_message()
        {
            IMessageSerializationService real = Build.A.SerializationService().WithDiscovery(_privateKey).TestObject;
            IMessageSerializationService service = Substitute.For<IMessageSerializationService>();
            IByteBuffer? serialized = null;
            service.ZeroSerialize(Arg.Any<PingMsg>(), Arg.Any<IByteBufferAllocator>()).Returns(ci =>
                serialized = real.ZeroSerialize(ci.Arg<PingMsg>(), UnpooledByteBufferAllocator.Default));
            StartUdpChannel("127.0.0.1", 10003, _kademliaAdaptersMocks[0], service);
            PingMsg message = new(_privateKey2.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, _address, _address2, new byte[32])
            {
                FarAddress = _address2
            };

            await _discoveryHandlers[^1].SendMsg(message);

            await SleepWhileWaiting();
            await _kademliaAdaptersMocks[1].Received(1).OnIncomingMsg(Arg.Any<PingMsg>());
            Assert.That(serialized?.ReferenceCount, Is.Zero);
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

        private (IKademliaAdapter Adapter, DiscoveryHandler Handler, List<byte[]> Forwarded, IMessageSerializationService Service) CreateHandler(
            NodeFilter? nodeFilter = null,
            int? globalInboundMessageBurst = null,
            int? inboundMessageQueueCapacity = null,
            int? inboundMessageWorkerCount = null,
            IMessageSerializationService? messageSerializationService = null,
            ILogManager? logManager = null)
        {
            IKademliaAdapter adapter = Substitute.For<IKademliaAdapter>();
            adapter.OnIncomingMsg(Arg.Any<DiscoveryMsg>()).Returns(Task.CompletedTask);
            IMessageSerializationService service = messageSerializationService ?? Build.A.SerializationService().WithDiscovery(_privateKey2).TestObject;
            DiscoveryHandler handler = new(
                adapter,
                service,
                Timestamper.Default,
                logManager ?? LimboLogs.Instance,
                nodeFilter,
                globalInboundMessageBurst,
                inboundMessageQueueCapacity,
                inboundMessageWorkerCount);
            List<byte[]> forwarded = [];
            handler.InitializeChannel(Substitute.For<IDatagramSocket>(), datagram =>
            {
                lock (forwarded)
                {
                    forwarded.Add(datagram.Buffer.ToArray());
                }

                datagram.Dispose();
            });
            return (adapter, handler, forwarded, service);
        }

        [Test]
        public void UndersizedPacketIsNotForwardedToDiscoveryManager()
        {
            (IKademliaAdapter adapter, DiscoveryHandler handler, List<byte[]> _, IMessageSerializationService _) = CreateHandler();

            byte[] data = new byte[50];
            IPEndPoint from = IPEndPoint.Parse("127.0.0.1:10000");
            handler.Receive(PooledUdpReceiveResult.Copy(data, from));

            _ = adapter.DidNotReceive().OnIncomingMsg(Arg.Any<DiscoveryMsg>());
        }

        [Test]
        public void ForwardsUnrecognizedMessageToNextHandler()
        {
            (IKademliaAdapter _, DiscoveryHandler handler, List<byte[]> forwarded, IMessageSerializationService _) = CreateHandler();
            byte[] data = [1, 2, 3];
            IPEndPoint from = IPEndPoint.Parse("127.0.0.1:10000");

            handler.Receive(PooledUdpReceiveResult.Copy(data, from));

            Assert.That(forwarded, Has.One.EqualTo(data));
        }

        [Test]
        public void ForwardsDiscv5MinimumSizePacketWithoutDebugLogging()
        {
            TestLogger logger = new() { IsTrace = false };
            (IKademliaAdapter _, DiscoveryHandler handler, List<byte[]> forwarded, IMessageSerializationService _) =
                CreateHandler(logManager: new OneLoggerLogManager(new ILogger(logger)));
            byte[] data = new byte[63];

            handler.Receive(PooledUdpReceiveResult.Copy(data, _address2));

            Assert.That(forwarded, Has.Count.EqualTo(1));
            Assert.That(logger.LogList, Is.Empty);
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

        [TestCase(-60, "has expired")]
        [TestCase(7200, "expires too far in the future")]
        public async Task InvalidExpirationIsLoggedAtTrace(long expirationOffsetSeconds, string expectedMessage)
        {
            TestLogger logger = new() { IsDebug = false };
            (IKademliaAdapter adapter, DiscoveryHandler handler, List<byte[]> forwarded, IMessageSerializationService service) =
                CreateHandler(logManager: new OneLoggerLogManager(new ILogger(logger)));
            byte[] data = SerializePing(service, expirationOffsetSeconds);

            handler.Receive(PooledUdpReceiveResult.Copy(data, _address2));
            await SleepWhileWaiting();

            Assert.That(logger.LogList, Has.Some.Contains(expectedMessage));
            _ = adapter.DidNotReceive().OnIncomingMsg(Arg.Any<DiscoveryMsg>());
        }

        [Test]
        public async Task EnrResponseWithoutExpirationIsAccepted()
        {
            (IKademliaAdapter adapter, DiscoveryHandler handler, List<byte[]> forwarded, IMessageSerializationService service) = CreateHandler();

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

            handler.Receive(PooledUdpReceiveResult.Copy(data, _address2));

            await SleepWhileWaiting();

            await adapter.Received(1).OnIncomingMsg(Arg.Is<DiscoveryMsg>(static m => m.MsgType == MsgType.EnrResponse));
            Assert.That(forwarded, Is.Empty);
        }

        [Test]
        public async Task RateLimitedMessagesAreIgnored()
        {
            (IKademliaAdapter adapter, DiscoveryHandler handler, List<byte[]> forwarded, IMessageSerializationService service) = CreateHandler(NodeFilter.CreateExact(16, TimeSpan.FromMinutes(1)));
            using SemaphoreSlim called = new(0);
            adapter.When(x => x.OnIncomingMsg(Arg.Any<DiscoveryMsg>())).Do(_ => called.Release());

            byte[] data = SerializePing(service);

            handler.Receive(PooledUdpReceiveResult.Copy((byte[])data.Clone(), _address2));
            handler.Receive(PooledUdpReceiveResult.Copy((byte[])data.Clone(), _address2));

            Assert.That(await called.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
            await Task.Delay(50);

            await adapter.Received(1).OnIncomingMsg(Arg.Any<DiscoveryMsg>());
            Assert.That(forwarded, Is.Empty);
        }

        [Test]
        public async Task DefaultInboundRateLimiter_Allows_ShortBurstFromSameIp()
        {
            (IKademliaAdapter adapter, DiscoveryHandler handler, List<byte[]> forwarded, IMessageSerializationService service) = CreateHandler();

            byte[] data = SerializePing(service);

            handler.Receive(PooledUdpReceiveResult.Copy((byte[])data.Clone(), _address2));
            handler.Receive(PooledUdpReceiveResult.Copy((byte[])data.Clone(), _address2));

            await SleepWhileWaiting();

            await adapter.Received(2).OnIncomingMsg(Arg.Any<DiscoveryMsg>());
            Assert.That(forwarded, Is.Empty);
        }

        [Test]
        public async Task DefaultInboundRateLimiter_Drops_Message_AboveBurstLimit()
        {
            (IKademliaAdapter adapter, DiscoveryHandler handler, List<byte[]> forwarded, IMessageSerializationService service) = CreateHandler();

            byte[] data = SerializePing(service);

            for (int i = 0; i < 9; i++)
            {
                handler.Receive(PooledUdpReceiveResult.Copy((byte[])data.Clone(), _address2));
            }

            await SleepWhileWaiting();

            await adapter.Received(8).OnIncomingMsg(Arg.Any<DiscoveryMsg>());
        }

        [TestCase("::ffff:127.0.0.2", "127.0.0.2")]
        [TestCase("2001:db8::2", "2001:db8::2")]
        public async Task DualStackSender_IsAcceptedInCanonicalForm(string senderAddress, string expectedAddress)
        {
            IKademliaAdapter adapter = Substitute.For<IKademliaAdapter>();
            TaskCompletionSource<DiscoveryMsg> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
            adapter.OnIncomingMsg(Arg.Any<DiscoveryMsg>()).Returns(callInfo =>
            {
                received.TrySetResult(callInfo.Arg<DiscoveryMsg>());
                return Task.CompletedTask;
            });
            IPEndPoint listener = new(IPAddress.IPv6Loopback, 10004);
            StartUdpChannel(listener.Address.ToString(), listener.Port, adapter, Build.A.SerializationService().WithDiscovery(_privateKey).TestObject);

            IPEndPoint sender = new(IPAddress.Parse(senderAddress), _address2.Port);
            byte[] data = SerializePing(Build.A.SerializationService().WithDiscovery(_privateKey2).TestObject);
            using IDatagramSocket senderSocket = _channelFactory.CreateDatagramSocket();
            senderSocket.Bind(sender);

            await senderSocket.SendToAsync(data, listener);

            DiscoveryMsg message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await adapter.Received(1).OnIncomingMsg(Arg.Any<DiscoveryMsg>());
            Assert.That(message.FarAddress, Is.EqualTo(new IPEndPoint(IPAddress.Parse(expectedAddress), sender.Port)));
        }

        [Test]
        public async Task GlobalInboundRateLimiter_Drops_Messages_AboveBurstLimit()
        {
            (IKademliaAdapter adapter, DiscoveryHandler handler, List<byte[]> forwarded, IMessageSerializationService service) = CreateHandler(globalInboundMessageBurst: 2);
            using SemaphoreSlim called = new(0);
            adapter.When(x => x.OnIncomingMsg(Arg.Any<DiscoveryMsg>())).Do(_ => called.Release());

            byte[] data = SerializePing(service);

            for (int i = 0; i < 3; i++)
            {
                IPEndPoint sender = new(IPAddress.Parse($"127.0.1.{i + 1}"), _address2.Port);
                handler.Receive(PooledUdpReceiveResult.Copy((byte[])data.Clone(), sender));
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
            (IKademliaAdapter adapter, DiscoveryHandler handler, List<byte[]> forwarded, IMessageSerializationService service) = CreateHandler(
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

            handler.Receive(PooledUdpReceiveResult.Copy((byte[])data.Clone(), new IPEndPoint(IPAddress.Parse("127.0.2.1"), _address2.Port)));
            Assert.That(deserializeEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);

            for (int i = 1; i < 16; i++)
            {
                IPEndPoint sender = new(IPAddress.Parse($"127.0.2.{i + 1}"), _address2.Port);
                handler.Receive(PooledUdpReceiveResult.Copy((byte[])data.Clone(), sender));
            }

            unblockDeserialize.Set();

            Assert.That(() => Interlocked.CompareExchange(ref received, 0, 0), Is.EqualTo(2).After(5000, 10));
        }

        private byte[] SerializePing(IMessageSerializationService service, long expirationOffsetSeconds = 1200)
        {
            PingMsg msg = new(_privateKey2.PublicKey, Timestamper.Default.UnixTime.SecondsLong + expirationOffsetSeconds, _address2, _address, new byte[32])
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

        private void StartUdpChannel(string address, int port, IKademliaAdapter kademliaAdapter, IMessageSerializationService service)
        {
            IPAddress bindAddress = IPAddress.Parse(address);
            DiscoveryConnectionsPool pool = new(
                LimboLogs.Instance.GetClassLogger<DiscoveryConnectionsPool>(),
                new DiscoveryConfig(),
                new NetworkListenerState(bindAddress, bindAddress, LimboLogs.Instance));
            DiscoveryHandler handler = new(kademliaAdapter, service, new Timestamper(), LimboLogs.Instance);
            IDatagramSocket socket = pool.Bind(_ => _channelFactory.CreateDatagramSocket(), port, handler.Receive);
            handler.InitializeChannel(socket, static datagram => datagram.Dispose());

            _pools.Add(pool);
            _discoveryHandlers.Add(handler);
            kademliaAdapter.MsgSender = handler;
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
            {
                if (typeof(T) == typeof(PingMsg) && Interlocked.Increment(ref _deserializeCalls) == 1)
                {
                    deserializeEntered.Set();
                    unblockDeserialize.Wait(TimeSpan.FromSeconds(10));
                }

                return innerService.Deserialize<T>(buffer);
            }
        }
    }
}
