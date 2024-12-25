using Nethermind.Blockchain;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Etha.Messages;
using Nethermind.Stats;

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
            // Implementation will be added in next step
        }

        private void Handle(ShardedBlocksMessage message)
        {
            // Implementation will be added in next step
        }

        private void Handle(NewShardedBlockMessage message)
        {
            // Implementation will be added in next step
        }
    }
}
