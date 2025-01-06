using Nethermind.Core;
using Nethermind.Network.P2P;

namespace Nethermind.Network.P2P.Subprotocols.Etha.Messages
{
    /// <summary>
    /// Message containing requested sharded blocks.
    /// </summary>
    public class ShardedBlocksMessage : P2PMessage
    {
        /// <summary>
        /// Gets the packet type for the message.
        /// </summary>
        public override int PacketType => EthaMessageCode.ShardedBlocks;

        /// <summary>
        /// Gets the protocol identifier.
        /// </summary>
        public override string Protocol => "etha";
        
        /// <summary>
        /// Gets the array of blocks being sent.
        /// </summary>
        public Block[] Blocks { get; }
        
        /// <summary>
        /// Initializes a new instance of the <see cref="ShardedBlocksMessage"/> class.
        /// </summary>
        /// <param name="blocks">The array of blocks to send.</param>
        public ShardedBlocksMessage(Block[] blocks)
        {
            Blocks = blocks;
        }
    }
} 
