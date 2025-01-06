using Nethermind.Core;
using Nethermind.Network.P2P;

namespace Nethermind.Network.P2P.Subprotocols.Etha.Messages
{
    /// <summary>
    /// Message announcing a new sharded block.
    /// </summary>
    public class NewShardedBlockMessage : P2PMessage
    {
        /// <summary>
        /// Gets the packet type for the message.
        /// </summary>
        public override int PacketType => EthaMessageCode.NewShardedBlock;

        /// <summary>
        /// Gets the protocol identifier.
        /// </summary>
        public override string Protocol => "etha";
        
        /// <summary>
        /// Gets the new block being announced.
        /// </summary>
        public Block Block { get; }
        
        /// <summary>
        /// Initializes a new instance of the <see cref="NewShardedBlockMessage"/> class.
        /// </summary>
        /// <param name="block">The new block to announce.</param>
        public NewShardedBlockMessage(Block block)
        {
            Block = block;
        }
    }
} 
