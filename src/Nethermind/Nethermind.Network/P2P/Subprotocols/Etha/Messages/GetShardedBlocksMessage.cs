using Nethermind.Core.Crypto;
using Nethermind.Network.P2P;

namespace Nethermind.Network.P2P.Subprotocols.Etha.Messages
{
    public class GetShardedBlocksMessage : P2PMessage
    {
        public override int PacketType => EthaMessageCode.GetShardedBlocks;
        
        public Hash256[] BlockHashes { get; set; }
        
        public GetShardedBlocksMessage(Hash256[] blockHashes)
        {
            BlockHashes = blockHashes;
        }
    }
} 
