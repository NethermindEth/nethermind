using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Etha.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Etha.Messages
{
    [TestFixture]
    public class EthaMessageSerializerTests
    {
        [Test]
        public void Can_serialize_and_deserialize_GetShardedBlocksMessage()
        {
            var serializer = new GetShardedBlocksMessageSerializer();
            var message = new GetShardedBlocksMessage(new[] { TestItem.KeccakA, TestItem.KeccakB });

            RlpStream rlpStream = new RlpStream();
            serializer.Serialize(rlpStream, message);

            var deserialized = serializer.Deserialize(new RlpStream(rlpStream.Data));
            deserialized.BlockHashes.Should().BeEquivalentTo(message.BlockHashes);
        }

        [Test]
        public void Can_serialize_and_deserialize_ShardedBlocksMessage()
        {
            var serializer = new ShardedBlocksMessageSerializer();
            var message = new ShardedBlocksMessage(new[] { Build.A.Block.TestObject });

            RlpStream rlpStream = new RlpStream();
            serializer.Serialize(rlpStream, message);

            var deserialized = serializer.Deserialize(new RlpStream(rlpStream.Data));
            deserialized.Blocks.Length.Should().Be(message.Blocks.Length);
            deserialized.Blocks[0].Hash.Should().Be(message.Blocks[0].Hash);
        }

        [Test]
        public void Can_serialize_and_deserialize_NewShardedBlockMessage()
        {
            var serializer = new NewShardedBlockMessageSerializer();
            var block = Build.A.Block.TestObject;
            var message = new NewShardedBlockMessage(block);

            RlpStream rlpStream = new RlpStream();
            serializer.Serialize(rlpStream, message);

            var deserialized = serializer.Deserialize(new RlpStream(rlpStream.Data));
            deserialized.Block.Hash.Should().Be(message.Block.Hash);
        }
    }
} 
