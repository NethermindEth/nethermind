using Nethermind.Core.Crypto;
using Nethermind.Network.P2P;

namespace Nethermind.Network.P2P.Subprotocols.Etha.Messages
{
    /// <summary>
    /// Message for requesting sharded blocks by their hashes.
    /// </summary>
    public class GetShardedBlocksMessage : P2PMessage
    {
        /// <summary>
        /// Gets the packet type for the message.
        /// </summary>
        public override int PacketType => EthaMessageCode.GetShardedBlocks;

        /// <summary>
        /// Gets the protocol identifier.
        /// </summary>
        public override string Protocol => "etha";
        
        /// <summary>
        /// Gets the array of block hashes to request.
        /// </summary>
        public Hash256[] BlockHashes { get; }
        
        /// <summary>
        /// Initializes a new instance of the <see cref="GetShardedBlocksMessage"/> class.
        /// </summary>
        /// <param name="blockHashes">The array of block hashes to request.</param>
        public GetShardedBlocksMessage(Hash256[] blockHashes)
        {
            BlockHashes = blockHashes;
        }
    }
} 
