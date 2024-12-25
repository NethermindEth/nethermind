using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Etha.Messages;
using Nethermind.Stats;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Etha
{
    [TestFixture]
    public class EthaProtocolIntegrationTests
    {
        private IBlockTree _blockTree;
        private IMessageSerializationService _serializationService;
        private INodeStatsManager _statsManager;
        private ILogManager _logManager;
        private EthaProtocolHandler _handler;
        private EthaProtocolFactory _factory;
        private ISession _session;

        [SetUp]
        public void Setup()
        {
            _blockTree = Substitute.For<IBlockTree>();
            _serializationService = Substitute.For<IMessageSerializationService>();
            _statsManager = Substitute.For<INodeStatsManager>();
            _logManager = LimboLogs.Instance;
            _session = Substitute.For<ISession>();
            
            _factory = new EthaProtocolFactory(
                _blockTree,
                _serializationService,
                _statsManager,
                _logManager);
        }

        [Test]
        public void Protocol_properly_initialized()
        {
            Protocol protocol = _factory.Create(_session);
            protocol.Should().NotBeNull();
            protocol.Name.Should().Be("etha");
            protocol.Version.Should().Be(1);
            protocol.MessageIdSpaceSize.Should().Be(3);
        }

        [Test]
        public void Can_handle_full_protocol_flow()
        {
            // Arrange
            Block block = Build.A.Block.WithNumber(1).TestObject;
            _blockTree.FindBlock(block.Hash).Returns(block);
            
            Protocol protocol = _factory.Create(_session);
            var handler = protocol.MessageHandlers[0] as EthaProtocolHandler;
            handler.Should().NotBeNull();

            // Act - request blocks
            var getMessage = new GetShardedBlocksMessage(new[] { block.Hash });
            handler!.HandleMessage(new Packet(EthaMessageCode.GetShardedBlocks, _serializationService.Serialize(getMessage)));

            // Assert - verify response
            _serializationService.Received().Serialize(Arg.Is<ShardedBlocksMessage>(m => 
                m.Blocks.Length == 1 && m.Blocks[0] == block));
        }
    }
} 
