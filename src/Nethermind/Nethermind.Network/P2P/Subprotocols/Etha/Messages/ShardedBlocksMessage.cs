using Nethermind.Core;
using Nethermind.Network.P2P;

namespace Nethermind.Network.P2P.Subprotocols.Etha.Messages
{
    public class ShardedBlocksMessage : P2PMessage
    {
        public override int PacketType => EthaMessageCode.ShardedBlocks;
        
        public Block[] Blocks { get; set; }
        
        public ShardedBlocksMessage(Block[] blocks)
        {
            Blocks = blocks;
        }
    }
}
