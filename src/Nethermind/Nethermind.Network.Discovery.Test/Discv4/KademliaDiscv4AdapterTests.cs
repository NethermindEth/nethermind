// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Enr;
using Nethermind.Network.Test.Builders;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv4
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class KademliaDiscv4AdapterTests
    {
        public enum NoResponseRequest
        {
            Ping,
            FindNeighbours,
            SendEnrRequest
        }

        private IKademliaDiscv4Adapter _adapter = null!;

        private IKademlia<PublicKey, Node> _kademliaMessageReceiver = null!;
        private INodeHealthTracker<Node> _nodeHealthTracker = null!;
        private INetworkConfig _networkConfig = null!;
        private KademliaConfig<Node> _kademliaConfig = null!;
        private NodeRecord _selfNodeRecord = null!;
        private ILogManager _logManager = null!;
        private ITimestamper _timestamper = null!;
        private IMsgSender _msgSender = null!;
        private INodeStatsManager _nodeStatsManager = null!;
        private Node _testNode = null!;
        private PublicKey _testPublicKey = null!;

        private IMessageSerializationService _receiverSerializationManager;
        private Node _receiver;

        private void ConfigureBondCallback() =>
            _msgSender
                .When(x => x.SendMsg(Arg.Any<PingMsg>()))
                .Do(ci =>
                {
                    PingMsg sent = (PingMsg)ci[0]!;
                    IByteBuffer buffer = _receiverSerializationManager.ZeroSerialize(sent);
                    PingMsg msg = _receiverSerializationManager.Deserialize<PingMsg>(buffer);
                    PongMsg pong = new(
                        msg.FarPublicKey!,
                        _timestamper.UnixTime.SecondsLong + 1,
                        sent.Mdc!);
                    pong.FarAddress = _receiver.Address;
                    Task.Run(() => _adapter.OnIncomingMsg(pong));
                });

        [SetUp]
        public void Setup()
        {
            // test node & dependencies
            _testPublicKey = TestItem.PublicKeyA;
            _testNode = new(_testPublicKey, "192.168.1.1", 30303);

            _kademliaMessageReceiver = Substitute.For<IKademlia<PublicKey, Node>>();
            _nodeHealthTracker = Substitute.For<INodeHealthTracker<Node>>();
            _networkConfig = Substitute.For<INetworkConfig>();
            _networkConfig.MaxActivePeers.Returns(25);
            _kademliaConfig = new KademliaConfig<Node> { CurrentNodeId = _testNode };

            _selfNodeRecord = CreateNodeRecord();

            _logManager = LimboLogs.Instance;
            _timestamper = Substitute.For<ITimestamper>();
            DateTime now = new(2021, 5, 3, 0, 0, 0, DateTimeKind.Utc);
            _timestamper.UtcNow.Returns(now);
            _timestamper.UnixTime.Returns(new UnixTime(now));
            _msgSender = Substitute.For<IMsgSender>();
            _msgSender.SendMsg(Arg.Any<DiscoveryMsg>()).Returns(Task.CompletedTask);

            _receiver = new(TestItem.PublicKeyB, "192.168.1.2", 30303);
            SerializationBuilder builder = new();
            builder.WithDiscovery(TestItem.PrivateKeyB);
            _receiverSerializationManager = builder.TestObject;

            INodeRecordProvider nodeRecordProvider = Substitute.For<INodeRecordProvider>();
            nodeRecordProvider.Current.Returns(_selfNodeRecord);
            _nodeStatsManager = Substitute.For<INodeStatsManager>();
            _nodeStatsManager.GetOrAdd(Arg.Any<Node>()).Returns(Substitute.For<INodeStats>());

            _adapter = new KademliaDiscv4Adapter(
                new Lazy<IKademlia<PublicKey, Node>>(() => _kademliaMessageReceiver),
                new Lazy<INodeHealthTracker<Node>>(() => _nodeHealthTracker),
                new DiscoveryConfig
                {
                    EnrTimeout = 100,
                    PingTimeout = 100,
                    SendNodeTimeout = 100,
                    BondWaitTime = 1,
                },
                _kademliaConfig,
                nodeRecordProvider,
                _nodeStatsManager,
                _timestamper,
                Substitute.For<IProcessExitSource>(),
                _logManager
            );
            _adapter.MsgSender = _msgSender;
        }

        [Test]
        public async Task GetSession_should_return_single_session_for_concurrent_calls()
        {
            Node[] nodes = Enumerable.Repeat(_receiver, 128).ToArray();

            NodeSession[] sessions = await Task.WhenAll(nodes.Select(node => Task.Run(() => _adapter.GetSession(node))));

            Assert.That(sessions.All(session => ReferenceEquals(session, sessions[0])), Is.True);
            _nodeStatsManager.Received(1).GetOrAdd(Arg.Is<Node>(node => node.Id == _receiver.Id));
        }

        private NodeRecord CreateNodeRecord()
        {
            NodeRecord selfNodeRecord = new();
            selfNodeRecord.SetEntry(IdEntry.Instance);
            selfNodeRecord.SetEntry(new IpEntry(IPAddress.Parse("192.168.1.1")));
            selfNodeRecord.SetEntry(new TcpEntry(_networkConfig.P2PPort));
            selfNodeRecord.SetEntry(new UdpEntry(_networkConfig.DiscoveryPort));
            selfNodeRecord.SetEntry(new SecP256k1Entry(TestItem.PrivateKeyA.CompressedPublicKey));
            selfNodeRecord.EnrSequence = 1;
            NodeRecordSigner enrSigner = new(new EthereumEcdsa(BlockchainIds.Mainnet), TestItem.PrivateKeyA);
            enrSigner.Sign(selfNodeRecord);
            if (!enrSigner.Verify(selfNodeRecord))
            {
                throw new NetworkingException("Self ENR initialization failed", NetworkExceptionType.Discovery);
            }

            return selfNodeRecord;
        }

        [TearDown]
        public async Task TearDown() => await _adapter.DisposeAsync();

        private T AddReceiverFarAddress<T>(T msg) where T : DiscoveryMsg
        {
            IByteBuffer buffer = _receiverSerializationManager.ZeroSerialize<T>(msg);
            IPEndPoint? farAddress = msg.FarAddress;
            msg = _receiverSerializationManager.Deserialize<T>(buffer);
            msg.FarAddress = farAddress;
            return msg;
        }

        private async Task<bool> HasResponse(NoResponseRequest request, CancellationToken token) =>
            request switch
            {
                NoResponseRequest.Ping => await _adapter.Ping(_receiver, token),
                NoResponseRequest.FindNeighbours => await _adapter.FindNeighbours(_receiver, TestItem.PublicKeyC, token) is not null,
                NoResponseRequest.SendEnrRequest => await _adapter.SendEnrRequest(_receiver, token) is not null,
                _ => throw new ArgumentOutOfRangeException(nameof(request), request, null)
            };

        [Test]
        [CancelAfter(10000)]
        public async Task Ping_should_send_ping_and_receive_pong(CancellationToken token)
        {
            ConfigureBondCallback();

            bool result = await _adapter.Ping(_receiver, token);

            Assert.That(result, Is.True);
            await _msgSender.Received(1).SendMsg(Arg.Is<PingMsg>(m =>
                m.FarAddress!.Equals(_receiver.Address)));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task FindNeighbours_should_return_nodes(CancellationToken token)
        {
            Node[] expected = Enumerable.Repeat(new Node(TestItem.PublicKeyD, "192.168.1.3", 30303), 16).ToArray();

            ConfigureBondCallback();

            _msgSender
                .When(x => x.SendMsg(Arg.Any<FindNodeMsg>()))
                .Do(ci =>
                {
                    ArraySegment<Node> neighbours1 = expected[..12];

                    NeighborsMsg neighbors = new(_receiver.Address, _timestamper.UnixTime.SecondsLong + 1, neighbours1);
                    neighbors = AddReceiverFarAddress(neighbors);
                    Task.Run(() => _adapter.OnIncomingMsg(neighbors));

                    ArraySegment<Node> neighbours2 = expected[12..];
                    NeighborsMsg neighbors2 = new(_receiver.Address, _timestamper.UnixTime.SecondsLong + 1, neighbours2);
                    neighbors2 = AddReceiverFarAddress(neighbors2);
                    Task.Run(() => _adapter.OnIncomingMsg(neighbors2));
                });

            Node[]? result = await _adapter.FindNeighbours(_receiver, TestItem.PublicKeyC, token);
            Assert.That(result, Is.EquivalentTo(expected));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task SendEnrRequest_should_ping_then_enr_request_and_return_response(CancellationToken token)
        {
            ConfigureBondCallback();

            byte[] requestHash = TestItem.KeccakA.BytesToArray();
            _msgSender
                .When(x => x.SendMsg(Arg.Any<EnrRequestMsg>()))
                .Do(ci =>
                {
                    EnrRequestMsg sent = (EnrRequestMsg)ci[0]!;
                    sent.Hash = requestHash;
                    EnrResponseMsg response = AddReceiverFarAddress(new EnrResponseMsg(_receiver.Address, _selfNodeRecord, new Hash256(requestHash)));
                    Task.Run(() => _adapter.OnIncomingMsg(response));
                });

            EnrResponseMsg? result = await _adapter.SendEnrRequest(_receiver, token);

            await _msgSender.Received(1).SendMsg(Arg.Is<EnrRequestMsg>(m => m.FarAddress!.Equals(_receiver.Address)));
            Assert.That(result?.NodeRecord.GetHex(), Is.EqualTo(_selfNodeRecord.GetHex()));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task SendEnrRequest_should_reject_unsolicited_response_with_wrong_keccak(CancellationToken token)
        {
            ConfigureBondCallback();

            _msgSender
                .When(x => x.SendMsg(Arg.Any<EnrRequestMsg>()))
                .Do(ci =>
                {
                    EnrRequestMsg sent = (EnrRequestMsg)ci[0]!;
                    sent.Hash = TestItem.KeccakA.BytesToArray();
                    EnrResponseMsg response = AddReceiverFarAddress(new EnrResponseMsg(_receiver.Address, _selfNodeRecord, TestItem.KeccakB));
                    Task.Run(() => _adapter.OnIncomingMsg(response));
                });

            EnrResponseMsg? result = await _adapter.SendEnrRequest(_receiver, token);

            Assert.That(result, Is.Null);
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Timed_out_response_handler_should_not_consume_later_unsolicited_message(CancellationToken token)
        {
            ConfigureBondCallback();

            PingMsg pingMsg = new(_receiver.Address, _timestamper.UnixTime.SecondsLong + 20, _kademliaConfig.CurrentNodeId.Address);
            pingMsg.FarAddress = _receiver.Address;
            pingMsg = AddReceiverFarAddress(pingMsg);
            await _adapter.OnIncomingMsg(pingMsg);

            EnrResponseMsg? result = await _adapter.SendEnrRequest(_receiver, token);

            Assert.That(result, Is.Null);

            _nodeHealthTracker.ClearReceivedCalls();

            EnrResponseMsg response = new(
                _receiver.Address,
                _selfNodeRecord,
                new(new byte[32]));
            response = AddReceiverFarAddress(response);

            await _adapter.OnIncomingMsg(response);

            _nodeHealthTracker.DidNotReceive().OnIncomingMessageFrom(Arg.Is<Node>(n => n.Id.Equals(_receiver.Id)));
        }

        [TestCase(NoResponseRequest.Ping)]
        [TestCase(NoResponseRequest.FindNeighbours)]
        [TestCase(NoResponseRequest.SendEnrRequest)]
        [CancelAfter(10000)]
        public async Task Request_timeout_should_return_no_response_and_record_failure_once(NoResponseRequest request, CancellationToken token)
        {
            if (request is not NoResponseRequest.Ping)
            {
                ConfigureBondCallback();
            }

            bool hasResponse = await HasResponse(request, token);

            Assert.That(hasResponse, Is.False);
            _nodeHealthTracker.Received(1).OnRequestFailed(Arg.Is<Node>(n => n.Id.Equals(_receiver.Id)));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task FindNeighbours_should_not_send_find_node_when_bond_ping_times_out(CancellationToken token)
        {
            Node[]? result = await _adapter.FindNeighbours(_receiver, TestItem.PublicKeyC, token);

            Assert.That(result, Is.Null);
            await _msgSender.Received(1).SendMsg(Arg.Is<DiscoveryMsg>(m => m is PingMsg));
            await _msgSender.DidNotReceive().SendMsg(Arg.Is<DiscoveryMsg>(m => m is FindNodeMsg));
        }

        [Test]
        [CancelAfter(10000)]
        public void Ping_should_throw_on_lifecycle_cancellation(CancellationToken token)
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.Cancel();

            Assert.CatchAsync<OperationCanceledException>(async () => await _adapter.Ping(_receiver, cts.Token));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Failed_send_should_remove_response_handler(CancellationToken token)
        {
            PingMsg? sent = null;
            _msgSender.SendMsg(Arg.Any<PingMsg>()).Returns(callInfo =>
            {
                sent = (PingMsg)callInfo[0]!;
                return Task.FromException(new InvalidOperationException("send failed"));
            });

            Assert.ThrowsAsync<InvalidOperationException>(async () => await _adapter.Ping(_receiver, token));
            Assert.That(sent, Is.Not.Null);
            sent = AddReceiverFarAddress(sent!);

            _nodeHealthTracker.ClearReceivedCalls();

            PongMsg response = new(_receiver.Address, _timestamper.UnixTime.SecondsLong + 1, sent!.Mdc!);
            response = AddReceiverFarAddress(response);
            await _adapter.OnIncomingMsg(response);

            _nodeHealthTracker.DidNotReceive().OnIncomingMessageFrom(Arg.Is<Node>(n => n.Id.Equals(_receiver.Id)));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task OnIncomingMsg_ping_should_respond_with_pong(CancellationToken token)
        {
            ConfigureBondCallback();

            PingMsg pingMsg = new(_receiver.Address, _timestamper.UnixTime.SecondsLong + 20, _kademliaConfig.CurrentNodeId.Address);
            pingMsg.FarAddress = _receiver.Address;
            pingMsg = AddReceiverFarAddress(pingMsg);

            await _adapter.OnIncomingMsg(pingMsg);

            await Task.Delay(100);

            await _msgSender.Received(1).SendMsg(Arg.Is<PongMsg>(m =>
                m.FarAddress!.Equals(_receiver.Address) &&
                m.PingMdc!.SequenceEqual(pingMsg.Mdc!)));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task OnIncomingMsg_find_node_should_respond_with_neighbors(CancellationToken token)
        {
            ConfigureBondCallback();
            Assert.That(await _adapter.Ping(_receiver, token), Is.True);
            _msgSender.ClearReceivedCalls();

            FindNodeMsg findNodeMsg = new(_receiver.Address, _timestamper.UnixTime.SecondsLong + 20, _testPublicKey.Bytes);
            findNodeMsg = AddReceiverFarAddress(findNodeMsg);

            Node[] expectedNodes = Enumerable.Repeat(new Node(TestItem.PublicKeyD, "192.168.1.3", 30303), 20).ToArray();
            _kademliaMessageReceiver.GetKNeighbour(
                Arg.Any<PublicKey>(),
                Arg.Any<Node>())
                .Returns(expectedNodes);

            await _adapter.OnIncomingMsg(findNodeMsg);

            await Task.Delay(100);

            _kademliaMessageReceiver.GetKNeighbour(
                Arg.Is<PublicKey>(pk => pk.Bytes!.SequenceEqual(_testPublicKey.Bytes!)),
                Arg.Is<Node>(n => n.Id == _receiver.Id));

            // Send out two messages instead of one because of MTU limit.
            await _msgSender.Received(1).SendMsg(Arg.Is<NeighborsMsg>(m =>
                m.FarAddress!.Equals(_receiver.Address) &&
                m.Nodes.Count == 12));
            await _msgSender.Received(1).SendMsg(Arg.Is<NeighborsMsg>(m =>
                m.FarAddress!.Equals(_receiver.Address) &&
                m.Nodes.Count == 8));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task OnIncomingMsg_enr_request_should_respond_with_enr_response(CancellationToken token)
        {
            ConfigureBondCallback();
            Assert.That(await _adapter.Ping(_receiver, token), Is.True);
            _msgSender.ClearReceivedCalls();

            EnrRequestMsg enrRequestMsg = new(_receiver.Address, _timestamper.UnixTime.SecondsLong + 20);
            enrRequestMsg = AddReceiverFarAddress(enrRequestMsg);

            await _adapter.OnIncomingMsg(enrRequestMsg);

            Task.Delay(100).Wait();

            await _msgSender.Received(1).SendMsg(Arg.Is<EnrResponseMsg>(m =>
                m.FarAddress!.Equals(_receiver.Address) &&
                m.NodeRecord.Equals(_selfNodeRecord)));
        }
    }
}
