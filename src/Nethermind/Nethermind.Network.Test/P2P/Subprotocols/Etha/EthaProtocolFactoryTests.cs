using Nethermind.Blockchain;
using Nethermind.Logging;
using Nethermind.Stats;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Etha
{
    [TestFixture]
    public class EthaProtocolFactoryTests
    {
        private IBlockTree _blockTree;
        private IMessageSerializationService _serializationService;
        private INodeStatsManager _nodeStatsManager;
        private ILogManager _logManager;
        private EthaProtocolFactory _factory;

        [SetUp]
        public void Setup()
        {
            _blockTree = Substitute.For<IBlockTree>();
            _serializationService = Substitute.For<IMessageSerializationService>();
            _nodeStatsManager = Substitute.For<INodeStatsManager>();
            _logManager = LimboLogs.Instance;
            
            _factory = new EthaProtocolFactory(
                _blockTree,
                _serializationService,
                _nodeStatsManager,
                _logManager);
        }

        [Test]
        public void Creates_protocol_with_correct_name()
        {
            Assert.That(_factory.Name, Is.EqualTo("etha"));
        }

        [Test]
        public void Creates_protocol_instance()
        {
            var session = Substitute.For<ISession>();
            var protocol = _factory.Create(session);
            
            Assert.That(protocol, Is.Not.Null);
            Assert.That(protocol.Name, Is.EqualTo("etha"));
        }
    }
} 
