//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network.Discovery;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.Rlpx;
using Nethermind.Specs;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;
using NSubstitute;
using NSubstitute.Exceptions;
using NUnit.Framework;

namespace Nethermind.Network.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class ProtocolsManagerTests
    {
        [SetUp]
        public void SetUp()
        {
        }

        public static Context When => new Context();

        public class Context
        {
            private int _localPort = 30312;
            private int _remotePort = 30000;
            private string _remoteHost = "35.0.0.1";
            private ISession _currentSession;
            private IDiscoveryApp _discoveryApp;
            private IRlpxPeer _localPeer;
            private ProtocolsManager _manager;
            private INodeStatsManager _nodeStatsManager;
            private INetworkStorage _peerStorage;
            private IProtocolValidator _protocolValidator;
            private IMessageSerializationService _serializer;
            private ISyncServer _syncServer;
            private ISyncPeerPool _syncPeerPool;
            private ITxPool _txPool;
            private IPooledTxsRequestor _pooledTxsRequestor;
            private IChannelHandlerContext _channelHandlerContext;
            private IChannel _channel;
            private IChannelPipeline _pipeline;
            private IPacketSender _packetSender;
            private IBlockTree _blockTree;

            public Context()
            {
                _channel = Substitute.For<IChannel>();
                _channelHandlerContext = Substitute.For<IChannelHandlerContext>();
                _pipeline = Substitute.For<IChannelPipeline>();
                _channelHandlerContext.Channel.Returns(_channel);
                _channel.Pipeline.Returns(_pipeline);
                _pipeline.Get<ZeroPacketSplitter>().Returns(new ZeroPacketSplitter(LimboLogs.Instance));
                _packetSender = Substitute.For<IPacketSender>();
                _syncServer = Substitute.For<ISyncServer>();
                _syncServer = Substitute.For<ISyncServer>();
                _syncServer.Genesis.Returns(Build.A.Block.Genesis.TestObject.Header);
                _syncServer.Head.Returns(Build.A.BlockHeader.TestObject);
                _txPool = Substitute.For<ITxPool>();
                _pooledTxsRequestor = Substitute.For<IPooledTxsRequestor>();
                _discoveryApp = Substitute.For<IDiscoveryApp>();
                _serializer = new MessageSerializationService();
                _localPeer = Substitute.For<IRlpxPeer>();
                _localPeer.LocalPort.Returns(_localPort);
                _localPeer.LocalNodeId.Returns(TestItem.PublicKeyA);
                ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
                _nodeStatsManager = new NodeStatsManager(timerFactory, LimboLogs.Instance);
                _blockTree = Substitute.For<IBlockTree>();
                _blockTree.ChainId.Returns(1ul);
                _blockTree.Genesis.Returns(Build.A.Block.Genesis.TestObject.Header);
                _protocolValidator = new ProtocolValidator(_nodeStatsManager, _blockTree, LimboLogs.Instance);
                _peerStorage = Substitute.For<INetworkStorage>();
                _syncPeerPool = Substitute.For<ISyncPeerPool>();
                _manager = new ProtocolsManager(
                    _syncPeerPool,
                    _syncServer,
                    _txPool,
                    _pooledTxsRequestor,
                    _discoveryApp,
                    _serializer,
                    _localPeer,
                    _nodeStatsManager,
                    _protocolValidator,
                    _peerStorage,
                    MainnetSpecProvider.Instance, 
                    LimboLogs.Instance);

                _serializer.Register(new HelloMessageSerializer());
                _serializer.Register(new StatusMessageSerializer());
                _serializer.Register(new DisconnectMessageSerializer());
            }

            public Context CreateIncomingSession()
            {
                IChannel channel = Substitute.For<IChannel>();
                _currentSession = new Session(_localPort, channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
                _pipeline.Get<ZeroNettyP2PHandler>().Returns(new ZeroNettyP2PHandler(_currentSession, LimboLogs.Instance));
                _localPeer.SessionCreated += Raise.EventWith(new object(), new SessionEventArgs(_currentSession));
                return this;
            }

            public Context CreateOutgoingSession()
            {
                IChannel channel = Substitute.For<IChannel>();
                _currentSession = new Session(_localPort, new Node(TestItem.PublicKeyB, _remoteHost, _remotePort) {AddedToDiscovery = true}, channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
                _pipeline.Get<ZeroNettyP2PHandler>().Returns(new ZeroNettyP2PHandler(_currentSession, LimboLogs.Instance));
                _localPeer.SessionCreated += Raise.EventWith(new object(), new SessionEventArgs(_currentSession));
                return this;
            }

            public Context Handshake()
            {
                _currentSession.Handshake(TestItem.PublicKeyB);
                return this;
            }

            public Context Init()
            {
                _currentSession.Init(5, _channelHandlerContext, _packetSender);
                return this;
            }

            public Context ActivateChannel()
            {
                _currentSession.RemoteHost = _remoteHost;
                _currentSession.RemotePort = _remotePort;
                return this;
            }

            public Context VerifyPingSenderSet()
            {
                Assert.NotNull(_currentSession.PingSender);
                return this;
            }

            public Context VerifyDisconnected()
            {
                Assert.AreEqual(SessionState.Disconnected, _currentSession.State);
                return this;
            }

            public Context ReceiveDisconnect()
            {
                DisconnectMessage message = new DisconnectMessage(DisconnectReason.Other);
                _currentSession.ReceiveMessage(new Packet("p2p", P2PMessageCode.Disconnect, _serializer.Serialize(message)));
                return this;
            }

            public Context Wait(int i)
            {
                Thread.Sleep(i);
                return this;
            }

            public Context VerifyInitialized()
            {
                Assert.AreEqual(SessionState.Initialized, _currentSession.State);
                return this;
            }

            public Context Disconnect()
            {
                _currentSession.MarkDisconnected(DisconnectReason.TooManyPeers, DisconnectType.Local, "test");
                return this;
            }

            public Context ReceiveStatus()
            {
                StatusMessage msg = new StatusMessage();
                msg.TotalDifficulty = 1;
                msg.ChainId = 1;
                msg.GenesisHash = _blockTree.Genesis.Hash;
                msg.BestHash = _blockTree.Genesis.Hash;
                msg.ProtocolVersion = 63;
                
                return ReceiveStatus(msg);
            }

            private Context ReceiveStatus(StatusMessage msg)
            {
                IByteBuffer statusPacket = _serializer.ZeroSerialize(msg);
                statusPacket.ReadByte();

                _currentSession.ReceiveMessage(new ZeroPacket(statusPacket) {PacketType = Eth62MessageCode.Status + 16});
                return this;
            }

            public Context VerifyEthInitialized()
            {
                INodeStats stats = _nodeStatsManager.GetOrAdd(_currentSession.Node);
                Assert.AreEqual(1, stats.EthNodeDetails.ChainId);
                Assert.AreEqual(_blockTree.Genesis.Hash, stats.EthNodeDetails.GenesisHash);
                Assert.AreEqual(63, stats.EthNodeDetails.ProtocolVersion);
                Assert.AreEqual(BigInteger.One, stats.EthNodeDetails.TotalDifficulty);
                return this;
            }

            public Context VerifySyncPeersRemoved()
            {
                _txPool.Received().RemovePeer(Arg.Any<PublicKey>());
                _syncPeerPool.Received().RemovePeer(Arg.Any<ISyncPeer>());
                return this;
            }

            public Context ReceiveHello(byte p2pVersion = 5)
            {
                HelloMessage msg = new HelloMessage();
                msg.Capabilities = new List<Capability> {new Capability("eth", 62)};
                msg.NodeId = TestItem.PublicKeyB;
                msg.ClientId = "other client v1";
                msg.P2PVersion = p2pVersion;
                msg.ListenPort = 30314;
                _currentSession.ReceiveMessage(new Packet("p2p", P2PMessageCode.Hello, _serializer.Serialize(msg)));
                return this;
            }
            
            public Context ReceiveHelloNoEth()
            {
                HelloMessage msg = new HelloMessage();
                msg.Capabilities = new List<Capability> { };
                msg.NodeId = TestItem.PublicKeyB;
                msg.ClientId = "other client v1";
                msg.P2PVersion = 5;
                msg.ListenPort = 30314;
                _currentSession.ReceiveMessage(new Packet("p2p", P2PMessageCode.Hello, _serializer.Serialize(msg)));
                return this;
            }
            
            public Context ReceiveHelloWrongEth()
            {
                HelloMessage msg = new HelloMessage();
                msg.Capabilities = new List<Capability> {new Capability("eth", 61)};
                msg.NodeId = TestItem.PublicKeyB;
                msg.ClientId = "other client v1";
                msg.P2PVersion = 5;
                msg.ListenPort = 30314;
                _currentSession.ReceiveMessage(new Packet("p2p", P2PMessageCode.Hello, _serializer.Serialize(msg)));
                return this;
            }

            public Context ReceiveStatusWrongChain()
            {
                StatusMessage msg = new StatusMessage();
                msg.TotalDifficulty = 1;
                msg.ChainId = 2;
                msg.GenesisHash = TestItem.KeccakA;
                msg.BestHash = TestItem.KeccakA;
                msg.ProtocolVersion = 63;

                return ReceiveStatus(msg);
            }

            public Context ReceiveStatusWrongGenesis()
            {
                StatusMessage msg = new StatusMessage();
                msg.TotalDifficulty = 1;
                msg.ChainId = 1;
                msg.GenesisHash = TestItem.KeccakB;
                msg.BestHash = TestItem.KeccakB;
                msg.ProtocolVersion = 63;

                return ReceiveStatus(msg);
            }

            public Context VerifyNotAddedToDiscovery()
            {
                _discoveryApp.DidNotReceive().AddNodeToDiscovery(_currentSession.Node);
                Assert.True(_currentSession.Node.AddedToDiscovery);
                return this;
            }
            
            public Context VerifyAddedToDiscovery()
            {
                _discoveryApp.Received().AddNodeToDiscovery(_currentSession.Node);
                Assert.True(_currentSession.Node.AddedToDiscovery);
                return this;
            }
        }

        [Test]
        public void Sets_ping_sender_after_receiving_hello()
        {
            When
                .CreateIncomingSession()
                .ActivateChannel()
                .Handshake()
                .Init()
                .ReceiveHello()
                .VerifyPingSenderSet();
        }

        [Test]
        public void Disconnects_on_p2p_before_version_4()
        {
            When
                .CreateIncomingSession()
                .ActivateChannel()
                .Handshake()
                .Init()
                .ReceiveHello(3)
                .VerifyDisconnected();
        }

        [Test]
        public void Disconnects_on_receiving_disconnect()
        {
            When
                .CreateIncomingSession()
                .ActivateChannel()
                .Handshake()
                .Init()
                .ReceiveHello(5)
                .ReceiveDisconnect()
                .VerifyDisconnected();
        }

        [Test]
        public void Runs_ok_when_initializing_protocol_on_a_closing_session()
        {
            When
                .CreateIncomingSession()
                .ActivateChannel()
                .Handshake()
                .Init()
                .Disconnect()
                .ReceiveHello(5);
        }

        [Test]
        public void Can_initialize_a_session()
        {
            When
                .CreateIncomingSession()
                .ActivateChannel()
                .Handshake()
                .Init()
                .VerifyInitialized();
        }

        [Test]
        public void Adds_to_discovery()
        {
            When
                .CreateIncomingSession()
                .ActivateChannel()
                .Handshake()
                .Init()
                .ReceiveHello()
                .VerifyAddedToDiscovery();
        }
        
        [Test]
        public void Skips_adding_to_discovery_when_already_known()
        {
            When
                .CreateOutgoingSession()
                .ActivateChannel()
                .Handshake()
                .Init()
                .ReceiveHello()
                .VerifyNotAddedToDiscovery();
        }

        [Test]
        public void Can_initialize_eth_protocol()
        {
            When
                .CreateIncomingSession()
                .ActivateChannel()
                .Handshake()
                .Init()
                .VerifyInitialized()
                .ReceiveHello()
                .ReceiveStatus()
                .VerifyEthInitialized();
        }

        [Test]
        public void Removes_sync_peers_on_disconnect()
        {
            When
                .CreateIncomingSession()
                .ActivateChannel()
                .Handshake()
                .Init()
                .VerifyInitialized()
                .ReceiveHello()
                .ReceiveStatus()
                .VerifyEthInitialized()
                .Disconnect()
                .VerifySyncPeersRemoved();
        }

        [Test]
        public void Disconnects_on_missing_eth()
        {
            When
                .CreateIncomingSession()
                .ActivateChannel()
                .Handshake()
                .Init()
                .VerifyInitialized()
                .ReceiveHelloNoEth()
                .VerifyDisconnected();
        }
        
        [Test]
        public void Disconnects_on_wrong_eth()
        {
            When
                .CreateIncomingSession()
                .ActivateChannel()
                .Handshake()
                .Init()
                .VerifyInitialized()
                .ReceiveHelloWrongEth()
                .VerifyDisconnected();
        }
        
        [Test]
        public void Disconnects_on_wrong_chain_id()
        {
            When
                .CreateIncomingSession()
                .ActivateChannel()
                .Handshake()
                .Init()
                .VerifyInitialized()
                .ReceiveHello()
                .ReceiveStatusWrongChain()
                .VerifyDisconnected();
        }
        
        [Test]
        public void Disconnects_on_wrong_genesis_hash()
        {
            When
                .CreateIncomingSession()
                .ActivateChannel()
                .Handshake()
                .Init()
                .VerifyInitialized()
                .ReceiveHello()
                .ReceiveStatusWrongGenesis()
                .VerifyDisconnected();
        }
    }
}
