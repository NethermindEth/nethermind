// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Enr;
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
        
        [TearDown]
        public async Task TearDown()
        {
            await _adapter.DisposeAsync();
        }
        private Lazy<IKademliaMessageReceiver<PublicKey, Node>> _kademliaMessageReceiver = null!;
        private INetworkConfig _networkConfig = null!;
        private KademliaConfig<Node> _kademliaConfig = null!;
        private NodeRecord _selfNodeRecord = null!;
        private ILogManager _logManager = null!;
        private ITimestamper _timestamper = null!;
        private IMsgSender _msgSender = null!;
        private Node _testNode = null!;
        private PublicKey _testPublicKey = null!;

        [SetUp]
        public void Setup()
        {
            _testPublicKey = TestItem.PublicKeyA;
            _testNode = new Node(_testPublicKey, "192.168.1.1", 30303);
            
            _kademliaMessageReceiver = new Lazy<IKademliaMessageReceiver<PublicKey, Node>>(() => 
                Substitute.For<IKademliaMessageReceiver<PublicKey, Node>>());
            
            _networkConfig = Substitute.For<INetworkConfig>();
            _networkConfig.MaxActivePeers.Returns(25);
            
            _kademliaConfig = new KademliaConfig<Node>();
            _kademliaConfig.CurrentNodeId = _testNode;
            
            _selfNodeRecord = Substitute.For<NodeRecord>();
            
            _logManager = LimboLogs.Instance;
            
            _timestamper = Substitute.For<ITimestamper>();
            _timestamper.UnixTime.Returns(new UnixTime(new DateTime(2021, 5, 3, 0, 0, 0, DateTimeKind.Utc)));
            
            _msgSender = Substitute.For<IMsgSender>();
            
            _adapter = new KademliaDiscv4Adapter(
                _kademliaMessageReceiver,
                _networkConfig,
                _kademliaConfig,
                _selfNodeRecord,
                _logManager,
                _timestamper);
            
            _adapter.MsgSender = _msgSender;
        }

        [Test]
        public async Task Ping_should_send_ping_message()
        {
            // Arrange
            Node receiver = new Node(TestItem.PublicKeyB, "192.168.1.2", 30303);
            
            // Act
            await _adapter.Ping(receiver, CancellationToken.None);
            
            // Assert
            await _msgSender.Received(1).SendMsg(Arg.Is<PingMsg>(m => 
                m.FarAddress!.Equals(receiver.Address) && 
                m.SourceAddress!.Equals(_kademliaConfig.CurrentNodeId.Address)));
        }

        [Test]
        public async Task FindNeighbours_should_send_find_node_message_and_return_nodes()
        {
            // Arrange
            Node receiver = new Node(TestItem.PublicKeyB, "192.168.1.2", 30303);
            PublicKey target = TestItem.PublicKeyC;
            Node[] expectedNodes = { new Node(TestItem.PublicKeyD, "192.168.1.3", 30303) };
            
            // Setup the message sender to respond with a pong when a ping is sent
            _msgSender.When(x => x.SendMsg(Arg.Any<PingMsg>()))
                .Do(x => 
                {
                    PingMsg pingMsg = (PingMsg)x[0];
                    PongMsg pongMsg = new PongMsg(receiver.Id, _timestamper.UnixTime.SecondsLong + 20, pingMsg.Mdc!);
                    _adapter.OnIncomingMsg(pongMsg);
                });
            
            // Setup the message sender to respond with neighbors when a find node is sent
            _msgSender.When(x => x.SendMsg(Arg.Any<FindNodeMsg>()))
                .Do(x => 
                {
                    FindNodeMsg findNodeMsg = (FindNodeMsg)x[0];
                    NeighborsMsg neighborsMsg = new NeighborsMsg(receiver.Id, _timestamper.UnixTime.SecondsLong + 20, expectedNodes);
                    _adapter.OnIncomingMsg(neighborsMsg);
                });
            
            // Act
            Node[] result = await _adapter.FindNeighbours(receiver, target, CancellationToken.None);
            
            // Assert
            await _msgSender.Received(1).SendMsg(Arg.Is<PingMsg>(m => m.FarAddress!.Equals(receiver.Address)));
            await _msgSender.Received(1).SendMsg(Arg.Is<FindNodeMsg>(m => 
                m.FarAddress!.Equals(receiver.Address) && 
                m.SearchedNodeId!.SequenceEqual(target.Bytes)));
            
            result.Should().BeEquivalentTo(expectedNodes);
        }

        [Test]
        public async Task SendEnrRequest_should_send_enr_request_message_and_return_response()
        {
            // Arrange
            Node receiver = new Node(TestItem.PublicKeyB, "192.168.1.2", 30303);
            EnrResponseMsg expectedResponse = new EnrResponseMsg(receiver.Id, _selfNodeRecord, new Hash256(new byte[32]));
            
            // Setup the message sender to respond with a pong when a ping is sent
            _msgSender.When(x => x.SendMsg(Arg.Any<PingMsg>()))
                .Do(x => 
                {
                    PingMsg pingMsg = (PingMsg)x[0];
                    PongMsg pongMsg = new PongMsg(receiver.Id, _timestamper.UnixTime.SecondsLong + 20, pingMsg.Mdc!);
                    _adapter.OnIncomingMsg(pongMsg);
                });
            
            // Setup the message sender to respond with ENR response when an ENR request is sent
            _msgSender.When(x => x.SendMsg(Arg.Any<EnrRequestMsg>()))
                .Do(x => 
                {
                    _adapter.OnIncomingMsg(expectedResponse);
                });
            
            // Act
            EnrResponseMsg result = await _adapter.SendEnrRequest(receiver, CancellationToken.None);
            
            // Assert
            await _msgSender.Received(1).SendMsg(Arg.Is<PingMsg>(m => m.FarAddress!.Equals(receiver.Address)));
            await _msgSender.Received(1).SendMsg(Arg.Is<EnrRequestMsg>(m => m.FarAddress!.Equals(receiver.Address)));
            
            result.Should().Be(expectedResponse);
        }

        [Test]
        public void OnIncomingMsg_ping_should_respond_with_pong()
        {
            // Arrange
            PingMsg pingMsg = new PingMsg(_testNode.Id, _timestamper.UnixTime.SecondsLong + 20, _kademliaConfig.CurrentNodeId.Address, _testNode.Address, new byte[32]);
            
            // Act
            _adapter.OnIncomingMsg(pingMsg);
            
            // Assert - Allow some time for the async operation to complete
            Task.Delay(100).Wait();
            
            _kademliaMessageReceiver.Value.Received(1).Ping(Arg.Is<Node>(n => n.Id == _testNode.Id), Arg.Any<CancellationToken>());
            _msgSender.Received(1).SendMsg(Arg.Is<PongMsg>(m => 
                m.FarAddress!.Equals(_testNode.Address) && 
                m.PingMdc!.SequenceEqual(pingMsg.Mdc!)));
        }

        [Test]
        public void OnIncomingMsg_find_node_should_respond_with_neighbors()
        {
            // Arrange
            FindNodeMsg findNodeMsg = new FindNodeMsg(_testNode.Id, _timestamper.UnixTime.SecondsLong + 20, _testPublicKey.Bytes);
            
            Node[] expectedNodes = { new Node(TestItem.PublicKeyD, "192.168.1.3", 30303) };
            _kademliaMessageReceiver.Value.FindNeighbours(
                Arg.Any<Node>(), 
                Arg.Any<PublicKey>(), 
                Arg.Any<CancellationToken>())
                .Returns(expectedNodes);
            
            // Act
            _adapter.OnIncomingMsg(findNodeMsg);
            
            // Assert - Allow some time for the async operation to complete
            Task.Delay(100).Wait();
            
            _kademliaMessageReceiver.Value.Received(1).FindNeighbours(
                Arg.Is<Node>(n => n.Id == _testNode.Id), 
                Arg.Is<PublicKey>(pk => pk.Bytes!.SequenceEqual(_testPublicKey.Bytes!)), 
                Arg.Any<CancellationToken>());
            
            _msgSender.Received(1).SendMsg(Arg.Is<NeighborsMsg>(m => 
                m.FarAddress!.Equals(_testNode.Address) && 
                m.Nodes.Length == expectedNodes.Length));
        }

        [Test]
        public void OnIncomingMsg_enr_request_should_respond_with_enr_response()
        {
            // Arrange
            EnrRequestMsg enrRequestMsg = new EnrRequestMsg(_testNode.Id, _timestamper.UnixTime.SecondsLong + 20);
            
            // Act
            _adapter.OnIncomingMsg(enrRequestMsg);
            
            // Assert - Allow some time for the async operation to complete
            Task.Delay(100).Wait();
            
            _msgSender.Received(1).SendMsg(Arg.Is<EnrResponseMsg>(m => 
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
                    _adapter.OnIncomingMsg(pongMsg);
                });
            
            // Act - Call a method that uses EnsureIncomingBondedPeer internally
            EnrRequestMsg enrRequestMsg = new EnrRequestMsg(receiver.Id, _timestamper.UnixTime.SecondsLong + 20);
            
            _adapter.OnIncomingMsg(enrRequestMsg);
            
            // Wait for async operations to complete
            await Task.Delay(100);
            
            // Assert - First call should send a ping
            await _msgSender.Received(1).SendMsg(Arg.Is<PingMsg>(m => m.FarAddress!.Equals(receiver.Address)));
            
            // Reset the received calls
            _msgSender.ClearReceivedCalls();
            
            // Act again - This should use the cached bond deadline
            _adapter.OnIncomingMsg(enrRequestMsg);
            
            // Wait for async operations to complete
            await Task.Delay(100);
            
            // Assert - Second call should not send a ping because the node is already bonded
            await _msgSender.DidNotReceive().SendMsg(Arg.Is<PingMsg>(m => m.FarAddress!.Equals(receiver.Address)));
        }
        
        [Test]
        public void IsPeerSafe_should_return_false_for_unbonded_peer()
        {
            // Arrange
            Node receiver = new Node(TestItem.PublicKeyB, "192.168.1.2", 30303);
            
            // Act
            bool isSafe = _adapter.IsPeerSafe(receiver);
            
            // Assert
            Assert.That(isSafe, Is.False, "Unbonded peer should not be considered safe");
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
                    _adapter.OnIncomingMsg(pongMsg);
                });
            
            // Act - Call a method that uses EnsureIncomingBondedPeer internally
            EnrRequestMsg enrRequestMsg = new EnrRequestMsg(receiver.Id, _timestamper.UnixTime.SecondsLong + 20);
            
            _adapter.OnIncomingMsg(enrRequestMsg);
            
            // Wait for async operations to complete
            await Task.Delay(100);
            
            // Act - Check if the peer is now safe
            bool isSafe = _adapter.IsPeerSafe(receiver);
            
            // Assert
            Assert.That(isSafe, Is.True, "Peer should be considered safe after ping/pong exchange");
        }
    }
}
