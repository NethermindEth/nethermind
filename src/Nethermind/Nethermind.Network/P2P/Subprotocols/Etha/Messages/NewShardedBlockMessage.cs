using Nethermind.Core;
using Nethermind.Network.P2P;

namespace Nethermind.Network.P2P.Subprotocols.Etha.Messages
{
    public class NewShardedBlockMessage : P2PMessage
    {
        public override int PacketType => EthaMessageCode.NewShardedBlock;
        
        public Block Block { get; set; }
        
        public NewShardedBlockMessage(Block block)
        {
            Block = block;
        }
    }
}
