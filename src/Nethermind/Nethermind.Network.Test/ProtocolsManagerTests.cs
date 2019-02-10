using DotNetty.Transport.Channels;
using Nethermind.Blockchain;
using Nethermind.Blockchain.TransactionPools;
using Nethermind.Core;
using Nethermind.Core.Logging;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.Discovery;
using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test
{
    [TestFixture]
    public class ProtocolsManagerTests
    {
        private INodeStatsManager _nodeStatsManager;
        private IProtocolValidator _protocolValidator;
        private IRlpxPeer _localPeer;
        private IDiscoveryApp _discoveryApp;
        private ITransactionPool _transactionPool;
        private ISynchronizationManager _synchronizationManager;
        private IMessageSerializationService _serializationService;
        private INetworkStorage _peerStorage;
        private IPerfService _perfService;
        private ProtocolsManager _manager;

        [SetUp]
        public void SetUp()
        {
            _synchronizationManager = Substitute.For<ISynchronizationManager>();
            _transactionPool = Substitute.For<ITransactionPool>();
            _discoveryApp = Substitute.For<IDiscoveryApp>();
            _serializationService = Substitute.For<IMessageSerializationService>();
            _localPeer = Substitute.For<IRlpxPeer>();
            _nodeStatsManager = new NodeStatsManager(new StatsConfig(), LimboLogs.Instance);
            _protocolValidator = new ProtocolValidator(_nodeStatsManager, 1, TestItem.KeccakA, LimboLogs.Instance);
            _peerStorage = Substitute.For<INetworkStorage>();
            _perfService = new PerfService(LimboLogs.Instance);
            _manager = new ProtocolsManager(
                _synchronizationManager,
                _transactionPool,
                _discoveryApp,
                _serializationService,
                _localPeer,
                _nodeStatsManager,
                _protocolValidator,
                _peerStorage,
                _perfService,
                LimboLogs.Instance);
        }

        [Test]
        public void Test()
        {
            P2PSession session = RaiseSessionCreated();
            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, Substitute.For<IChannelHandlerContext>(), Substitute.For<IPacketSender>());
          
        }

        private P2PSession RaiseSessionCreated()
        {
            IChannel channel = Substitute.For<IChannel>();
            P2PSession session = new P2PSession(TestItem.PublicKeyA, TestItem.PublicKeyB, 30312, ConnectionDirection.In, LimboLogs.Instance, channel);
            _localPeer.SessionCreated += Raise.EventWith(new object(), new SessionEventArgs(session));
            return session;
        }
    }
}