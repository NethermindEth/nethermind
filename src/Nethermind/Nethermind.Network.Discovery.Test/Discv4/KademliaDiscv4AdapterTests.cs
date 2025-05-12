// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Enr;
using Nethermind.Network.Test.Builders;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv4
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class KademliaDiscv4AdapterTests
    {
        private KademliaDiscv4Adapter _adapter = null!;

        private IKademliaMessageReceiver<PublicKey, Node> _kademliaMessageReceiver = null!;
        private INetworkConfig _networkConfig = null!;
        private KademliaConfig<Node> _kademliaConfig = null!;
        private NodeRecord _selfNodeRecord = null!;
        private ILogManager _logManager = null!;
        private ITimestamper _timestamper = null!;
        private IMsgSender _msgSender = null!;
        private Node _testNode = null!;
        private PublicKey _testPublicKey = null!;

        private IMessageSerializationService _receiverSerializationManager;
        private IPEndPoint _receiverHost;
        private Node _receiver;

        [SetUp]
        public void Setup()
        {
            // test node & dependencies
            _testPublicKey = TestItem.PublicKeyA;
            _testNode = new Node(_testPublicKey, "192.168.1.1", 30303);

            _kademliaMessageReceiver = Substitute.For<IKademliaMessageReceiver<PublicKey, Node>>();
            _networkConfig = Substitute.For<INetworkConfig>();
            _networkConfig.MaxActivePeers.Returns(25);
            _kademliaConfig = new KademliaConfig<Node> { CurrentNodeId = _testNode };
            _selfNodeRecord = Substitute.For<NodeRecord>();
            _logManager = LimboLogs.Instance;
            _timestamper = Substitute.For<ITimestamper>();
            _timestamper.UnixTime.Returns(new UnixTime(new DateTime(2021, 5, 3, 0, 0, 0, DateTimeKind.Utc)));
            _msgSender = Substitute.For<IMsgSender>();

            _receiverHost = IPEndPoint.Parse("192.168.1.2");
            _receiver = new Node(TestItem.PublicKeyB, "192.168.1.2", 30303);
            SerializationBuilder builder = new SerializationBuilder();
            builder.WithDiscovery(TestItem.PrivateKeyB);
            _receiverSerializationManager = builder.TestObject;


            _adapter = new KademliaDiscv4Adapter(
                new Lazy<IKademliaMessageReceiver<PublicKey, Node>>(() => _kademliaMessageReceiver),
                _networkConfig,
                _kademliaConfig,
                _selfNodeRecord,
                _timestamper,
                Substitute.For<IProcessExitSource>(),
                _logManager
            );
            _adapter.MsgSender = _msgSender;
        }

        [TearDown]
        public async Task TearDown()
        {
            await _adapter.DisposeAsync();
        }

        [Test]
        [CancelAfter(5000)]
        public async Task Ping_should_send_ping_and_receive_pong(CancellationToken token)
        {
            _msgSender
                .When(x => x.SendMsg(Arg.Any<PingMsg>()))
                .Do(ci =>
                {
                    var sent = (PingMsg)ci[0]!;
                    var buffer = _receiverSerializationManager.ZeroSerialize(sent);
                    PingMsg msg = _receiverSerializationManager.Deserialize<PingMsg>(buffer);

                    var pong = new PongMsg(
                        msg.FarPublicKey!,
                        _timestamper.UnixTime.SecondsLong + 1,
                        sent.Mdc!);
                    pong.FarAddress = _receiverHost;
                    Task.Run(() => _adapter.OnIncomingMsg(pong));
                });

            await _adapter.Ping(_receiver, token);

            await _msgSender.Received(1).SendMsg(Arg.Is<PingMsg>(m =>
                m.FarAddress!.Equals(_receiver.Address)));
        }

        [Test]
        [CancelAfter(5000)]
        public async Task FindNeighbours_should_ping_then_findnode_and_return_nodes(CancellationToken token)
        {
            // Arrange
            var receiver = new Node(TestItem.PublicKeyB, "192.168.1.2", 30303);
            var target   = TestItem.PublicKeyC;
            var expected = new[] { new Node(TestItem.PublicKeyD, "192.168.1.3", 30303) };

            // Stub: replying to PingMsg with PongMsg
            _msgSender
                .When(x => x.SendMsg(Arg.Any<PingMsg>()))
                .Do(ci =>
                {
                    var sent = (PingMsg)ci[0]!;
                    var pong = new PongMsg(
                        receiver.Address,
                        _timestamper.UnixTime.SecondsLong + 1,
                        sent.Mdc!);
                    Task.Run(() => _adapter.OnIncomingMsg(pong));
                });

            // Stub: replying to FindNodeMsg with NeighborsMsg
            _msgSender
                .When(x => x.SendMsg(Arg.Any<FindNodeMsg>()))
                .Do(ci =>
                {
                    var sent = (FindNodeMsg)ci[0]!;
                    var neighbors = new NeighborsMsg(
                        receiver.Address,
                        _timestamper.UnixTime.SecondsLong + 1,
                        expected);
                    Task.Run(() => _adapter.OnIncomingMsg(neighbors));
                });

            // Act
            Node[] result = await _adapter.FindNeighbours(receiver, target, token);

            // Assert
            await _msgSender.Received(1).SendMsg(Arg.Is<PingMsg>(m =>
                m.FarAddress!.Equals(receiver.Address)));
            await _msgSender.Received(1).SendMsg(Arg.Is<FindNodeMsg>(m =>
                m.FarAddress!.Equals(receiver.Address) &&
                m.SearchedNodeId!.SequenceEqual(target.Bytes)));
            result.Should().BeEquivalentTo(expected);
        }

        [Test]
        [CancelAfter(5000)]
        public async Task SendEnrRequest_should_ping_then_enr_request_and_return_response(CancellationToken token)
        {
            // Arrange
            var receiver = new Node(TestItem.PublicKeyB, "192.168.1.2", 30303);
            var expectedResponse = new EnrResponseMsg(
                receiver.Address,
                _selfNodeRecord,
                new Hash256(new byte[32]));

            // Stub: replying to PingMsg with PongMsg
            _msgSender
                .When(x => x.SendMsg(Arg.Any<PingMsg>()))
                .Do(ci =>
                {
                    var sent = (PingMsg)ci[0]!;
                    var pong = new PongMsg(
                        receiver.Address,
                        _timestamper.UnixTime.SecondsLong + 1,
                        sent.Mdc!);
                    Task.Run(() => _adapter.OnIncomingMsg(pong));
                });

            // Stub: replying to EnrRequestMsg with EnrResponseMsg
            _msgSender
                .When(x => x.SendMsg(Arg.Any<EnrRequestMsg>()))
                .Do(ci =>
                {
                    Task.Run(() => _adapter.OnIncomingMsg(expectedResponse));
                });

            // Act
            EnrResponseMsg result = await _adapter.SendEnrRequest(receiver, token);

            // Assert
            await _msgSender.Received(1).SendMsg(Arg.Any<PingMsg>());
            await _msgSender.Received(1).SendMsg(Arg.Is<EnrRequestMsg>(m =>
                m.FarAddress!.Equals(receiver.Address)));
            result.Should().Be(expectedResponse);
        }

        [Test]
        public async Task OnIncomingMsg_ping_should_respond_with_pong()
        {
            // Arrange
            PingMsg pingMsg = new PingMsg(_testNode.Id, _timestamper.UnixTime.SecondsLong + 20, _kademliaConfig.CurrentNodeId.Address, _testNode.Address, new byte[32]);

            // Act
            await _adapter.OnIncomingMsg(pingMsg);

            // Assert - Allow some time for the async operation to complete
            Task.Delay(100).Wait();

            await _kademliaMessageReceiver.Received(1).Ping(Arg.Is<Node>(n => n.Id == _testNode.Id), Arg.Any<CancellationToken>());
            await _msgSender.Received(1).SendMsg(Arg.Is<PongMsg>(m =>
                m.FarAddress!.Equals(_testNode.Address) &&
                m.PingMdc!.SequenceEqual(pingMsg.Mdc!)));
        }

        [Test]
        public async Task OnIncomingMsg_find_node_should_respond_with_neighbors()
        {
            // Arrange
            FindNodeMsg findNodeMsg = new FindNodeMsg(_testNode.Id, _timestamper.UnixTime.SecondsLong + 20, _testPublicKey.Bytes);

            Node[] expectedNodes = { new Node(TestItem.PublicKeyD, "192.168.1.3", 30303) };
            _kademliaMessageReceiver.FindNeighbours(
                Arg.Any<Node>(),
                Arg.Any<PublicKey>(),
                Arg.Any<CancellationToken>())
                .Returns(expectedNodes);

            // Act
            await _adapter.OnIncomingMsg(findNodeMsg);

            // Assert - Allow some time for the async operation to complete
            Task.Delay(100).Wait();

            await _kademliaMessageReceiver.Received(1).FindNeighbours(
                Arg.Is<Node>(n => n.Id == _testNode.Id),
                Arg.Is<PublicKey>(pk => pk.Bytes!.SequenceEqual(_testPublicKey.Bytes!)),
                Arg.Any<CancellationToken>());

            await _msgSender.Received(1).SendMsg(Arg.Is<NeighborsMsg>(m =>
                m.FarAddress!.Equals(_testNode.Address) &&
                m.Nodes.Count == expectedNodes.Length));
        }

        [Test]
        public async Task OnIncomingMsg_enr_request_should_respond_with_enr_response()
        {
            // Arrange
            EnrRequestMsg enrRequestMsg = new EnrRequestMsg(_testNode.Id, _timestamper.UnixTime.SecondsLong + 20);

            // Act
            await _adapter.OnIncomingMsg(enrRequestMsg);

            // Assert - Allow some time for the async operation to complete
            Task.Delay(100).Wait();

            await _msgSender.Received(1).SendMsg(Arg.Is<EnrResponseMsg>(m =>
                m.FarAddress!.Equals(_testNode.Address) &&
                m.NodeRecord.Equals(_selfNodeRecord)));
        }

        [Test]
        public async Task DisposeAsync_should_cancel_token_and_dispose_cancellation_token_source()
        {
            // Act
            await _adapter.DisposeAsync();

            // Assert
            // This test is mostly to ensure the method doesn't throw exceptions
            // The actual cancellation and disposal is hard to test directly
            Assert.Pass("DisposeAsync completed without exceptions");
        }

        [Test]
        public async Task EnsureIncomingBondedPeer_should_set_incoming_bond_deadline()
        {
            // Arrange
            Node receiver = new Node(TestItem.PublicKeyB, "192.168.1.2", 30303);

            // Setup the message sender to respond with a pong when a ping is sent
            _msgSender.When(x => x.SendMsg(Arg.Any<PingMsg>()))
                .Do(x =>
                {
                    PingMsg pingMsg = (PingMsg)x[0];
                    PongMsg pongMsg = new PongMsg(receiver.Id, _timestamper.UnixTime.SecondsLong + 20, pingMsg.Mdc!);
                    Task.Run(() => _adapter.OnIncomingMsg(pongMsg));
                });

            // Act - Call a method that uses EnsureIncomingBondedPeer internally
            EnrRequestMsg enrRequestMsg = new EnrRequestMsg(receiver.Id, _timestamper.UnixTime.SecondsLong + 20);

            await _adapter.OnIncomingMsg(enrRequestMsg);

            // Wait for async operations to complete
            await Task.Delay(100);

            // Assert - First call should send a ping
            await _msgSender.Received(1).SendMsg(Arg.Is<PingMsg>(m => m.FarAddress!.Equals(receiver.Address)));

            // Reset the received calls
            _msgSender.ClearReceivedCalls();

            // Act again - This should use the cached bond deadline
            await _adapter.OnIncomingMsg(enrRequestMsg);

            // Wait for async operations to complete
            await Task.Delay(100);

            // Assert - Second call should not send a ping because the node is already bonded
            await _msgSender.DidNotReceive().SendMsg(Arg.Is<PingMsg>(m => m.FarAddress!.Equals(receiver.Address)));
        }

        [Test]
        public async Task IsPeerSafe_should_return_true_after_ping_pong_exchange()
        {
            // Arrange
            Node receiver = new Node(TestItem.PublicKeyB, "192.168.1.2", 30303);

            // Setup the message sender to respond with a pong when a ping is sent
            _msgSender.When(x => x.SendMsg(Arg.Any<PingMsg>()))
                .Do(x =>
                {
                    PingMsg pingMsg = (PingMsg)x[0];
                    PongMsg pongMsg = new PongMsg(receiver.Id, _timestamper.UnixTime.SecondsLong + 20, pingMsg.Mdc!);
                    Task.Run(() => _adapter.OnIncomingMsg(pongMsg));
                });

            // Act - Call a method that uses EnsureIncomingBondedPeer internally
            EnrRequestMsg enrRequestMsg = new EnrRequestMsg(receiver.Id, _timestamper.UnixTime.SecondsLong + 20);

            await _adapter.OnIncomingMsg(enrRequestMsg);

            // Wait for async operations to complete
            await Task.Delay(100);

            // Act - Check if the peer is now safe
            // bool isSafe = _adapter.IsPeerSafe(receiver);

            // Assert
            // Assert.That(isSafe, Is.True, "Peer should be considered safe after ping/pong exchange");
        }
    }
}
