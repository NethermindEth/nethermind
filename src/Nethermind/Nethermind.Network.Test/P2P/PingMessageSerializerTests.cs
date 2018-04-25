using Nethermind.Network.P2P;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    [TestFixture]
    public class PingMessageSerializerTests
    {
        [Test]
        public void Can_do_roundtrip()
        {
            PingMessage msg = PingMessage.Instance;
            PingMessageSerializer serializer = new PingMessageSerializer();
            byte[] serialized = serializer.Serialize(msg);
            Assert.AreEqual(0xc0, serialized[0]);
            PingMessage deserialized = serializer.Deserialize(serialized);
            Assert.NotNull(deserialized);
        }
    }
}