using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth
{
    [TestFixture]
    public class GetBlockBodiesMessageSerializerTests
    {
        [Test]
        public void Roundtrip()
        {
            GetBlockBodiesMessageSerializer serializer = new GetBlockBodiesMessageSerializer();
            GetBlockBodiesMessage message = new GetBlockBodiesMessage(Keccak.OfAnEmptySequenceRlp, Keccak.Zero, Keccak.EmptyTreeHash);
            byte[] bytes = serializer.Serialize(message);
            GetBlockBodiesMessage deserialized = serializer.Deserialize(bytes);
            Assert.AreEqual(message.BlockHashes.Length, deserialized.BlockHashes.Length, $"length");
            for (int i = 0; i < message.BlockHashes.Length; i++)
            {
                Assert.AreEqual(message.BlockHashes[i], deserialized.BlockHashes[i], $"hash {i}");
            }
        }
    }
}