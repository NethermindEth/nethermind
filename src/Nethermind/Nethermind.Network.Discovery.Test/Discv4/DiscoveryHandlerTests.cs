// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections.Generic;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Core.Test.Threading;
using Nethermind.Network.Discovery.Discv5.Packets;
using TalkReqMsg = Nethermind.Network.Discovery.Discv5.Messages.TalkReqMsg;
using RequestId = Nethermind.Network.Discovery.Discv5.Messages.RequestId;
using Nethermind.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Discv4.Kademlia;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Network.Enr;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test;
using Nethermind.Network.Test.Builders;
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
            ReturnCountingPool pool = new();
            service.Serialize(Arg.Any<PingMsg>()).Returns(ci =>
            {
                byte[] packet = real.SerializeToArray(ci.Arg<PingMsg>());
                ArrayPoolSpan<byte> serialized = new(pool, packet.Length);
                packet.CopyTo((Span<byte>)serialized);
                return serialized;
            });
            StartUdpChannel("127.0.0.1", 10003, _kademliaAdaptersMocks[0], service);
            PingMsg message = new(_privateKey2.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, _address, _address2, new byte[32])
            {
                FarAddress = _address2
            };

            TaskCompletionSource received = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _kademliaAdaptersMocks[1].OnIncomingMsg(Arg.Any<PingMsg>()).Returns(_ =>
            {
                received.TrySetResult();
                return Task.CompletedTask;
            });

            await _discoveryHandlers[^1].SendMsg(message);

            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await _kademliaAdaptersMocks[1].Received(1).OnIncomingMsg(Arg.Any<PingMsg>());
            Assert.That(pool.Returned, Is.EqualTo(1));
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
            FindNodeMsg msg = new(_privateKey2.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, _privateKey2.PublicKey.Bytes)
            {
                FarAddress = _address2
            };

            await _discoveryHandlers[0].SendMsg(msg);
            await SleepWhileWaiting();
            await _kademliaAdaptersMocks[1].Received(1).OnIncomingMsg(Arg.Is<DiscoveryMsg>(static x => x.MsgType == MsgType.FindNode));

            FindNodeMsg msg2 = new(_privateKey2.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, _privateKey2.PublicKey.Bytes)
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
            ILogManager? logManager = null,
            FixedWindowLimiter? globalLimiter = null)
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
                inboundMessageWorkerCount)
            { GlobalInboundLimiter = globalLimiter };
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

            using (Assert.EnterMultipleScope())
            {
                Assert.That(forwarded, Has.Count.EqualTo(1));
                Assert.That(logger.LogList, Is.Empty);
            }
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
        public void InvalidExpirationIsLoggedAtTraceAndForwarded(long expirationOffsetSeconds, string expectedMessage)
        {
            TestLogger logger = new() { IsDebug = false };
            (IKademliaAdapter adapter, DiscoveryHandler handler, List<byte[]> forwarded, IMessageSerializationService service) =
                CreateHandler(logManager: new OneLoggerLogManager(new ILogger(logger)));
            byte[] data = SerializePing(service, expirationOffsetSeconds);

            handler.Receive(PooledUdpReceiveResult.Copy(data, _address2));

            Assert.That(() => Snapshot(forwarded), Has.One.EqualTo(data).After(5000, 10));
            Assert.That(logger.LogList, Has.Some.Contains(expectedMessage));
            _ = adapter.DidNotReceive().OnIncomingMsg(Arg.Any<DiscoveryMsg>());
        }

        [Test]
        public void ForwardsUndecodablePacketWithDiscv4Type()
        {
            (IKademliaAdapter adapter, DiscoveryHandler handler, List<byte[]> forwarded, IMessageSerializationService _) = CreateHandler();
            byte[] data = new byte[120];
            data[97] = (byte)MsgType.Ping;

            handler.Receive(PooledUdpReceiveResult.Copy(data, _address2));

            Assert.That(() => Snapshot(forwarded), Has.One.EqualTo(data).After(5000, 10));
            _ = adapter.DidNotReceive().OnIncomingMsg(Arg.Any<DiscoveryMsg>());
        }

        private static byte[][] Snapshot(List<byte[]> forwarded)
        {
            lock (forwarded)
            {
                return [.. forwarded];
            }
        }

        [Test]
        public async Task EnrResponseWithoutExpirationIsAccepted()
        {
            (IKademliaAdapter adapter, DiscoveryHandler handler, List<byte[]> forwarded, IMessageSerializationService service) = CreateHandler();

            EnrResponseMsg msg = BuildEnrResponse(_privateKey2);
            byte[] data = service.SerializeToArray(msg);

            handler.Receive(PooledUdpReceiveResult.Copy(data, _address2));

            await SleepWhileWaiting();

            await adapter.Received(1).OnIncomingMsg(Arg.Is<DiscoveryMsg>(static m => m.MsgType == MsgType.EnrResponse));
            Assert.That(forwarded, Is.Empty);
        }

        [Test]
        public async Task Rejected_source_should_not_spend_global_allowance()
        {
            FixedWindowLimiter global = new(2, TimeSpan.FromMilliseconds(100), new ManualTimeProvider());
            (IKademliaAdapter adapter, DiscoveryHandler handler, List<byte[]> forwarded, IMessageSerializationService service) =
                CreateHandler(NodeFilter.CreateExact(16, TimeSpan.FromMinutes(1)), globalLimiter: global);
            byte[] data = SerializePing(service);
            IPEndPoint honest = IPEndPoint.Parse("127.0.0.3:10002");
            using SemaphoreSlim received = new(0);
            adapter.When(x => x.OnIncomingMsg(Arg.Any<DiscoveryMsg>())).Do(_ => received.Release());

            for (int i = 0; i < 32; i++) handler.Receive(PooledUdpReceiveResult.Copy(data, _address2));
            handler.Receive(PooledUdpReceiveResult.Copy(data, honest));

            Assert.That(await received.WaitAsync(TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(await received.WaitAsync(TimeSpan.FromSeconds(2)), Is.True);
            await adapter.Received(1).OnIncomingMsg(Arg.Is<DiscoveryMsg>(msg => msg.FarAddress!.Equals(honest)));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(global.TryAcquire(), Is.False);
                Assert.That(forwarded, Is.Empty);
            }
            handler.CloseInbound();
        }

        [Test]
        public void Discv5_packet_with_discv4_type_should_bypass_exhausted_limits([Values] bool exhaustGlobal)
        {
            FixedWindowLimiter global = new(1, TimeSpan.FromMilliseconds(100), new ManualTimeProvider());
            NodeFilter source = NodeFilter.CreateExact(16, TimeSpan.FromMinutes(1));
            if (exhaustGlobal) Assert.That(global.TryAcquire(), Is.True);
            else Assert.That(source.TryAccept(_address2.Address), Is.True);
            (IKademliaAdapter _, DiscoveryHandler handler, List<byte[]> forwarded, IMessageSerializationService _) =
                CreateHandler(source, globalLimiter: global);
            using CryptoRandom random = new();
            using PacketCodec codec = new(new InsecureProtectedPrivateKey(TestItem.PrivateKeyA), random, new EthereumEcdsa(0));
            using TalkReqMsg request = new(new RequestId(1, 1), ReadOnlyMemory<byte>.Empty, new byte[64]);
            byte[] nonce = new byte[PacketCodec.NonceSize];
            byte[] packet = [];
            for (int i = 0; i < 4096; i++)
            {
                BitConverter.TryWriteBytes(nonce.AsSpan(), i);
                packet = codec.EncodeOrdinary(TestItem.PublicKeyB, new byte[16], request, nonce);
                if (packet[97] is >= 1 and <= 6) break;
            }
            Assert.That(packet[97], Is.InRange(1, 6));

            handler.Receive(PooledUdpReceiveResult.Copy(packet, _address2));

            Assert.That(forwarded, Has.One.EqualTo(packet));
            handler.CloseInbound();
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
        public async Task GlobalInboundRateLimiter_IsNotSpentByAHostThePerIpLimiterRejects()
        {
            // The per-IP burst is 8; a single host sending far more must not use up the global budget for everyone else.
            (IKademliaAdapter adapter, DiscoveryHandler handler, List<byte[]> forwarded, IMessageSerializationService service) = CreateHandler(globalInboundMessageBurst: 10);
            using SemaphoreSlim called = new(0);
            adapter.When(x => x.OnIncomingMsg(Arg.Any<DiscoveryMsg>())).Do(_ => called.Release());

            byte[] data = SerializePing(service);
            for (int i = 0; i < 100; i++)
            {
                handler.Receive(PooledUdpReceiveResult.Copy((byte[])data.Clone(), _address2));
            }

            IPEndPoint otherHost = new(IPAddress.Parse("127.0.3.1"), _address2.Port);
            handler.Receive(PooledUdpReceiveResult.Copy((byte[])data.Clone(), otherHost));

            for (int i = 0; i < 9; i++)
            {
                Assert.That(await called.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
            }
            await Task.Delay(50);

            await adapter.Received(9).OnIncomingMsg(Arg.Any<DiscoveryMsg>());
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

            return service.SerializeToArray(msg);
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

        private sealed class ReturnCountingPool : ArrayPool<byte>
        {
            private int _returned;

            public int Returned => Volatile.Read(ref _returned);

            public override byte[] Rent(int minimumLength) => new byte[minimumLength];

            public override void Return(byte[] array, bool clearArray = false) => Interlocked.Increment(ref _returned);
        }

        private sealed class BlockingSerializationService(
            IMessageSerializationService innerService,
            ManualResetEventSlim deserializeEntered,
            ManualResetEventSlim unblockDeserialize) : IMessageSerializationService
        {
            private int _deserializeCalls;

            public ArrayPoolSpan<byte> Serialize<T>(T message) where T : MessageBase
                => innerService.Serialize(message);

            public void Serialize<T>(T message, IBufferWriter<byte> writer) where T : MessageBase
                => innerService.Serialize(message, writer);

            public T Deserialize<T>(ArraySegment<byte> bytes) where T : MessageBase
            {
                if (typeof(T) == typeof(PingMsg) && Interlocked.Increment(ref _deserializeCalls) == 1)
                {
                    deserializeEntered.Set();
                    unblockDeserialize.Wait(TimeSpan.FromSeconds(10));
                }

                return innerService.Deserialize<T>(bytes);
            }

            public T Deserialize<T>(ZeroPacket packet) where T : MessageBase => innerService.Deserialize<T>(packet);
        }

        [Test]
        public async Task Failed_send_is_neutral_and_never_credited([Values("serialization", "socket", "success")] string outcome)
        {
            IMessageSerializationService real = Build.A.SerializationService().WithDiscovery(TestItem.PrivateKeyA).TestObject;
            IMessageSerializationService service = Substitute.For<IMessageSerializationService>();
            ReturnCountingPool pool = new();
            int serializedLength = 0;
            service.Serialize(Arg.Any<PingMsg>()).Returns(call =>
            {
                if (outcome == "serialization") throw new InvalidOperationException("serialization failed");
                byte[] bytes = real.SerializeToArray(call.Arg<PingMsg>());
                serializedLength = bytes.Length;
                ArrayPoolSpan<byte> serialized = new(pool, bytes.Length);
                bytes.CopyTo((Span<byte>)serialized);
                return serialized;
            });
            IDatagramSocket socket = Substitute.For<IDatagramSocket>();
            socket.SendToAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<IPEndPoint>(), Arg.Any<CancellationToken>())
                .Returns(outcome == "socket" ? ValueTask.FromException(new SocketException((int)SocketError.NetworkUnreachable)) : ValueTask.CompletedTask);
            DiscoveryHandler handler = new(Substitute.For<IDiscoveryMsgListener>(), service, Timestamper.Default, LimboLogs.Instance);
            handler.InitializeChannel(socket, static packet => packet.Dispose());
            IPEndPoint local = new(IPAddress.Loopback, 30303);
            IPEndPoint remote = new(IPAddress.Parse("8.8.8.8"), 30303);
            PingMsg message = new(TestItem.PublicKeyB, Timestamper.Default.UnixTime.SecondsLong + 20, local, remote, new byte[32])
            {
                FarAddress = remote
            };
            long before = Metrics.DiscoveryBytesSent;

            if (outcome == "success") await handler.SendMsg(message);
            else await Assert.ThrowsAsync<KademliaRequestNotSentException>(() => handler.SendMsg(message));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Metrics.DiscoveryBytesSent - before, Is.EqualTo(outcome == "success" ? serializedLength : 0));
                Assert.That(pool.Returned, Is.EqualTo(outcome == "serialization" ? 0 : 1));
            }
        }

    }
}
