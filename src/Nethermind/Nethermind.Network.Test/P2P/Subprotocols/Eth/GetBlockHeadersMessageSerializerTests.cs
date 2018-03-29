using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth
{
    [TestFixture]
    public class GetBlockHeadersMessageSerializerTests
    {
        [Test]
        public void Roundtrip()
        {
            GetBlockHeadersMessage message = new GetBlockHeadersMessage();
            message.MaxHeaders = 1;
            message.Skip = 2;
            message.Reverse = 1;
            message.StartingBlock = (100, Keccak.OfAnEmptyString);
            GetBlockHeadersMessageSerializer serializer = new GetBlockHeadersMessageSerializer();
            byte[] bytes = serializer.Serialize(message);
            GetBlockHeadersMessage deserialized = serializer.Deserialize(bytes);
            Assert.AreEqual(message.StartingBlock.Hash, deserialized.StartingBlock.Hash, $"{nameof(message.StartingBlock.Hash)}");
            Assert.AreEqual(message.StartingBlock.Number, deserialized.StartingBlock.Number, $"{nameof(message.StartingBlock.Number)}");
            Assert.AreEqual(message.MaxHeaders, deserialized.MaxHeaders, $"{nameof(message.MaxHeaders)}");
            Assert.AreEqual(message.Reverse, deserialized.Reverse, $"{nameof(message.Reverse)}");
            Assert.AreEqual(message.Skip, deserialized.Skip, $"{nameof(message.Skip)}");
        }
    }
}