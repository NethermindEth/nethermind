using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Etha.Messages
{
    public class GetShardedBlocksMessageSerializer : IMessageSerializer<GetShardedBlocksMessage>
    {
        public void Serialize(RlpStream rlpStream, GetShardedBlocksMessage message)
        {
            rlpStream.StartSequence(message.BlockHashes.Length);
            for (int i = 0; i < message.BlockHashes.Length; i++)
            {
                rlpStream.Write(message.BlockHashes[i]);
            }
        }

        public GetShardedBlocksMessage Deserialize(RlpStream rlpStream)
        {
            int hashesCount = rlpStream.ReadSequenceLength();
            var hashes = new Hash256[hashesCount];
            for (int i = 0; i < hashesCount; i++)
            {
                hashes[i] = rlpStream.ReadKeccak();
            }
            
            return new GetShardedBlocksMessage(hashes);
        }
    }

    public class ShardedBlocksMessageSerializer : IMessageSerializer<ShardedBlocksMessage>
    {
        private readonly BlockDecoder _blockDecoder;

        public ShardedBlocksMessageSerializer()
        {
            _blockDecoder = new BlockDecoder();
        }

        public void Serialize(RlpStream rlpStream, ShardedBlocksMessage message)
        {
            rlpStream.StartSequence(message.Blocks.Length);
            for (int i = 0; i < message.Blocks.Length; i++)
            {
                _blockDecoder.Encode(rlpStream, message.Blocks[i]);
            }
        }

        public ShardedBlocksMessage Deserialize(RlpStream rlpStream)
        {
            int blocksCount = rlpStream.ReadSequenceLength();
            var blocks = new Block[blocksCount];
            for (int i = 0; i < blocksCount; i++)
            {
                blocks[i] = _blockDecoder.Decode(rlpStream);
            }
            
            return new ShardedBlocksMessage(blocks);
        }
    }

    public class NewShardedBlockMessageSerializer : IMessageSerializer<NewShardedBlockMessage>
    {
        private readonly BlockDecoder _blockDecoder;

        public NewShardedBlockMessageSerializer()
        {
            _blockDecoder = new BlockDecoder();
        }

        public void Serialize(RlpStream rlpStream, NewShardedBlockMessage message)
        {
            _blockDecoder.Encode(rlpStream, message.Block);
        }

        public NewShardedBlockMessage Deserialize(RlpStream rlpStream)
        {
            Block block = _blockDecoder.Decode(rlpStream);
            return new NewShardedBlockMessage(block);
        }
    }
}
