using Nethermind.Network.P2P.Subprotocols.Eth;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth
{
    [TestFixture]
    public class NewBlockMessageSerializerTests
    {
        [Test]
        public void Roundtrip()
        {
            NewBlockMessage message = new NewBlockMessage();
            message.TotalDifficulty = 131200;
            NewBlockMessageSerializer serializer = new NewBlockMessageSerializer();
            byte[] bytes = serializer.Serialize(message);
            NewBlockMessage deserialized = serializer.Deserialize(bytes);
            Assert.AreEqual(message.Block, deserialized.Block, "block");
            Assert.AreEqual(message.TotalDifficulty, deserialized.TotalDifficulty, "total difficulty");
        }
    }
}