using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Etha.Messages;
using Nethermind.Stats;
using System.Linq;

namespace Nethermind.Network.P2P.Subprotocols.Etha
{
    public class EthaProtocolHandler : ProtocolHandlerBase
    {
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;

        public EthaProtocolHandler(
            IBlockTree blockTree,
            IMessageSerializationService serializer,
            INodeStatsManager nodeStats,
            ILogManager logManager) 
            : base(serializer, nodeStats, logManager)
        {
            _blockTree = blockTree;
            _logger = logManager.GetClassLogger();
        }

        public override void HandleMessage(Packet packet)
        {
            switch(packet.PacketType)
            {
                case EthaMessageCode.GetShardedBlocks:
                    Handle(Deserialize<GetShardedBlocksMessage>(packet.Data));
                    break;
                case EthaMessageCode.ShardedBlocks:
                    Handle(Deserialize<ShardedBlocksMessage>(packet.Data));
                    break;
                case EthaMessageCode.NewShardedBlock:
                    Handle(Deserialize<NewShardedBlockMessage>(packet.Data));
                    break;
                default:
                    _logger.Error($"Unsupported packet type: {packet.PacketType}");
                    break;
            }
        }

        private void Handle(GetShardedBlocksMessage message)
        {
            Block[] blocks = message.BlockHashes
                .Select(hash => _blockTree.FindBlock(hash))
                .Where(block => block != null)
                .ToArray();

            Send(new ShardedBlocksMessage(blocks));
        }

        private void Handle(ShardedBlocksMessage message)
        {
            foreach (Block block in message.Blocks)
            {
                if (_blockTree.IsKnown(block.Hash))
                {
                    continue;
                }

                // Process and suggest the new block to the block tree
                _blockTree.SuggestBlock(block);
            }
        }

        private void Handle(NewShardedBlockMessage message)
        {
            if (!_blockTree.IsKnown(message.Block.Hash))
            {
                _blockTree.SuggestBlock(message.Block);
            }
        }
    }
} 
