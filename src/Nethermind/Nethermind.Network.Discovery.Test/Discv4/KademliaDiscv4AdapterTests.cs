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
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Enr;
using Nethermind.Network.Test.Builders;
using Nethermind.Specs;
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
        private IKademliaDiscv4Adapter _adapter = null!;

        private IKademlia<PublicKey, Node> _kademliaMessageReceiver = null!;
        private INodeHealthTracker<Node> _nodeHealthTracker = null!;
        private INetworkConfig _networkConfig = null!;
        private KademliaConfig<Node> _kademliaConfig = null!;
        private NodeRecord _selfNodeRecord = null!;
        private ILogManager _logManager = null!;
        private ITimestamper _timestamper = null!;
        private IMsgSender _msgSender = null!;
        private Node _testNode = null!;
        private PublicKey _testPublicKey = null!;

        private IMessageSerializationService _receiverSerializationManager;
        private Node _receiver;

        private void ConfigureBondCallback()
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
                    pong.FarAddress = _receiver.Address;
                    Task.Run(() => _adapter.OnIncomingMsg(pong));
                });
        }

        [SetUp]
        public void Setup()
        {
            // test node & dependencies
            _testPublicKey = TestItem.PublicKeyA;
            _testNode = new Node(_testPublicKey, "192.168.1.1", 30303);

            _kademliaMessageReceiver = Substitute.For<IKademlia<PublicKey, Node>>();
            _nodeHealthTracker = Substitute.For<INodeHealthTracker<Node>>();
            _networkConfig = Substitute.For<INetworkConfig>();
            _networkConfig.MaxActivePeers.Returns(25);
            _kademliaConfig = new KademliaConfig<Node> { CurrentNodeId = _testNode };

            _selfNodeRecord = CreateNodeRecord(); ;

            _logManager = LimboLogs.Instance;
            _timestamper = Substitute.For<ITimestamper>();
            _timestamper.UnixTime.Returns(new UnixTime(new DateTime(2021, 5, 3, 0, 0, 0, DateTimeKind.Utc)));
            _msgSender = Substitute.For<IMsgSender>();

            _receiver = new Node(TestItem.PublicKeyB, "192.168.1.2", 30303);
            SerializationBuilder builder = new SerializationBuilder();
            builder.WithDiscovery(TestItem.PrivateKeyB);
            _receiverSerializationManager = builder.TestObject;

            INodeRecordProvider nodeRecordProvider = Substitute.For<INodeRecordProvider>();
            nodeRecordProvider.Current.Returns(_selfNodeRecord);

            _adapter = new KademliaDiscv4Adapter(
                new Lazy<IKademlia<PublicKey, Node>>(() => _kademliaMessageReceiver),
                new Lazy<INodeHealthTracker<Node>>(() => _nodeHealthTracker),
                new DiscoveryConfig(),
                _kademliaConfig,
                nodeRecordProvider,
                Substitute.For<INodeStatsManager>(),
                _timestamper,
                Substitute.For<IProcessExitSource>(),
                _logManager
            );
            _adapter.MsgSender = _msgSender;
        }

        private NodeRecord CreateNodeRecord()
        {
            NodeRecord selfNodeRecord = new();
            selfNodeRecord.SetEntry(IdEntry.Instance);
            selfNodeRecord.SetEntry(new IpEntry(IPAddress.Parse("192.168.1.1")));
            selfNodeRecord.SetEntry(new TcpEntry(_networkConfig.P2PPort));
            selfNodeRecord.SetEntry(new UdpEntry(_networkConfig.DiscoveryPort));
            selfNodeRecord.SetEntry(new Secp256K1Entry(TestItem.PrivateKeyA.CompressedPublicKey));
            selfNodeRecord.EnrSequence = 1;
            NodeRecordSigner enrSigner = new(new EthereumEcdsa(new MainnetSpecProvider()), TestItem.PrivateKeyA);
            enrSigner.Sign(selfNodeRecord);
            if (!enrSigner.Verify(selfNodeRecord))
            {
                throw new NetworkingException("Self ENR initialization failed", NetworkExceptionType.Discovery);
            }

            return selfNodeRecord;
        }

        [TearDown]
        public async Task TearDown()
        {
            await _adapter.DisposeAsync();
        }

        private T AddReceiverFarAddress<T>(T msg) where T : DiscoveryMsg
        {
            var buffer = _receiverSerializationManager.ZeroSerialize<T>(msg);
            var farAddress = msg.FarAddress;
            msg = _receiverSerializationManager.Deserialize<T>(buffer);
            msg.FarAddress = farAddress;
            return msg;
        }

        [Test]
        [CancelAfter(10000)]
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
                    pong.FarAddress = _receiver.Address;
                    Task.Run(() => _adapter.OnIncomingMsg(pong));
                });

            await _adapter.Ping(_receiver, token);

            await _msgSender.Received(1).SendMsg(Arg.Is<PingMsg>(m =>
                m.FarAddress!.Equals(_receiver.Address)));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task FindNeighbours_should_return_nodes(CancellationToken token)
        {
            var expected = Enumerable.Repeat(new Node(TestItem.PublicKeyD, "192.168.1.3", 30303), 16).ToArray();

            ConfigureBondCallback();

            _msgSender
                .When(x => x.SendMsg(Arg.Any<FindNodeMsg>()))
                .Do(ci =>
                {
                    ArraySegment<Node> neighbours1 = expected[..12];

                    var neighbors = new NeighborsMsg(_receiver.Address, _timestamper.UnixTime.SecondsLong + 1, neighbours1);
                    neighbors = AddReceiverFarAddress(neighbors);
                    Task.Run(() => _adapter.OnIncomingMsg(neighbors));

                    ArraySegment<Node> neighbours2 = expected[12..];
                    var neighbors2 = new NeighborsMsg(_receiver.Address, _timestamper.UnixTime.SecondsLong + 1, neighbours2);
                    neighbors2 = AddReceiverFarAddress(neighbors2);
                    Task.Run(() => _adapter.OnIncomingMsg(neighbors2));
                });

            Node[] result = await _adapter.FindNeighbours(_receiver, TestItem.PublicKeyC, token);
            result.Should().BeEquivalentTo(expected);
        }

        [Test]
        [CancelAfter(10000)]
        public async Task SendEnrRequest_should_ping_then_enr_request_and_return_response(CancellationToken token)
        {
            var expectedResponse = new EnrResponseMsg(
                _receiver.Address,
                _selfNodeRecord,
                new Hash256(new byte[32]));

            ConfigureBondCallback();

            _msgSender
                .When(x => x.SendMsg(Arg.Any<EnrRequestMsg>()))
                .Do(ci =>
                {
                    var response = AddReceiverFarAddress(expectedResponse);
                    Task.Run(() => _adapter.OnIncomingMsg(response));
                });

            EnrResponseMsg result = await _adapter.SendEnrRequest(_receiver, token);

            await _msgSender.Received(1).SendMsg(Arg.Is<EnrRequestMsg>(m => m.FarAddress!.Equals(_receiver.Address)));
            result.NodeRecord.Should().BeEquivalentTo(_selfNodeRecord);
        }

        [Test]
        [CancelAfter(10000)]
        public async Task OnIncomingMsg_ping_should_respond_with_pong(CancellationToken token)
        {
            PingMsg pingMsg = new PingMsg(_receiver.Address, _timestamper.UnixTime.SecondsLong + 20, _kademliaConfig.CurrentNodeId.Address);
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

            FindNodeMsg findNodeMsg = new FindNodeMsg(_receiver.Address, _timestamper.UnixTime.SecondsLong + 20, _testPublicKey.Bytes);
            findNodeMsg = AddReceiverFarAddress(findNodeMsg);

            Node[] expectedNodes = Enumerable.Repeat(new Node(TestItem.PublicKeyD, "192.168.1.3", 30303), 16).ToArray();
            _kademliaMessageReceiver.GetKNeighbour(
                Arg.Any<PublicKey>(),
                Arg.Any<Node>())
                .Returns(expectedNodes);

            await _adapter.OnIncomingMsg(findNodeMsg);

            await Task.Delay(100);

            _kademliaMessageReceiver.GetKNeighbour(
                Arg.Is<PublicKey>(pk => pk.Bytes!.SequenceEqual(_testPublicKey.Bytes!)),
                Arg.Is<Node>(n => n.Id == _receiver.Id));

            // Send out two message instead of one because of MTU limit.
            await _msgSender.Received(1).SendMsg(Arg.Is<NeighborsMsg>(m =>
                m.FarAddress!.Equals(_receiver.Address) &&
                m.Nodes.Count == 12));
            await _msgSender.Received(1).SendMsg(Arg.Is<NeighborsMsg>(m =>
                m.FarAddress!.Equals(_receiver.Address) &&
                m.Nodes.Count == 4));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task OnIncomingMsg_enr_request_should_respond_with_enr_response(CancellationToken token)
        {
            ConfigureBondCallback();

            EnrRequestMsg enrRequestMsg = new EnrRequestMsg(_receiver.Address, _timestamper.UnixTime.SecondsLong + 20);
            enrRequestMsg = AddReceiverFarAddress(enrRequestMsg);

            await _adapter.OnIncomingMsg(enrRequestMsg);

            Task.Delay(100).Wait();

            await _msgSender.Received(1).SendMsg(Arg.Is<EnrResponseMsg>(m =>
                m.FarAddress!.Equals(_receiver.Address) &&
                m.NodeRecord.Equals(_selfNodeRecord)));
        }
    }
}
