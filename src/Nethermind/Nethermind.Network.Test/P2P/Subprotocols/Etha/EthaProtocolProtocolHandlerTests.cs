using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Etha.Messages;
using Nethermind.Stats;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Etha
{
    [TestFixture]
    public class EthaProtocolHandlerTests
    {
        private IBlockTree _blockTree;
        private IMessageSerializationService _serializationService;
        private INodeStatsManager _statsManager;
        private ILogManager _logManager;
        private EthaProtocolHandler _handler;

        [SetUp]
        public void Setup()
        {
            _blockTree = Substitute.For<IBlockTree>();
            _serializationService = Substitute.For<IMessageSerializationService>();
            _statsManager = Substitute.For<INodeStatsManager>();
            _logManager = LimboLogs.Instance;
            
            _handler = new EthaProtocolHandler(
                _blockTree,
                _serializationService,
                _statsManager,
                _logManager);
        }

        [Test]
        public void Handle_GetShardedBlocks_returns_requested_blocks()
        {
            // Arrange
            Block block = Build.A.Block.WithNumber(1).TestObject;
            _blockTree.FindBlock(block.Hash).Returns(block);
            
            var message = new GetShardedBlocksMessage(new[] { block.Hash });

            // Act
            _handler.HandleMessage(new Packet(EthaMessageCode.GetShardedBlocks, _serializationService.Serialize(message)));

            // Assert
            _serializationService.Received().Serialize(Arg.Is<ShardedBlocksMessage>(m => 
                m.Blocks.Length == 1 && m.Blocks[0] == block));
        }

        [Test]
        public void Handle_ShardedBlocks_suggests_new_blocks()
        {
            // Arrange
            Block block = Build.A.Block.WithNumber(1).TestObject;
            _blockTree.IsKnown(block.Hash).Returns(false);
            
            var message = new ShardedBlocksMessage(new[] { block });

            // Act
            _handler.HandleMessage(new Packet(EthaMessageCode.ShardedBlocks, _serializationService.Serialize(message)));

            // Assert
            _blockTree.Received().SuggestBlock(block);
        }

        [Test]
        public void Handle_NewShardedBlock_suggests_block_if_unknown()
        {
            // Arrange
            Block block = Build.A.Block.WithNumber(1).TestObject;
            _blockTree.IsKnown(block.Hash).Returns(false);
            
            var message = new NewShardedBlockMessage(block);

            // Act
            _handler.HandleMessage(new Packet(EthaMessageCode.NewShardedBlock, _serializationService.Serialize(message)));

            // Assert
            _blockTree.Received().SuggestBlock(block);
        }

        [Test]
        public void Handle_NewShardedBlock_ignores_known_block()
        {
            // Arrange
            Block block = Build.A.Block.WithNumber(1).TestObject;
            _blockTree.IsKnown(block.Hash).Returns(true);
            
            var message = new NewShardedBlockMessage(block);

            // Act
            _handler.HandleMessage(new Packet(EthaMessageCode.NewShardedBlock, _serializationService.Serialize(message)));

            // Assert
            _blockTree.DidNotReceive().SuggestBlock(block);
        }
    }
} 
