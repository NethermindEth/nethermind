using Nevermind.Core.Crypto;
using Nevermind.Discovery.Messages;
using NUnit.Framework;

namespace Nevermind.Discovery.Test
{
    [TestFixture]
    public class DiscoveryMessageSerializerTests
    {
        private readonly PrivateKey _privateKey = new PrivateKey("49a7b37aa6f6645917e7b807e9d1c00d4fa71f18343b0d4122a4d2df64dd6fee");

        [Test]
        public void Find_nodes_there_and_back()
        {
            ISigner signer = new Signer();
            DiscoveryMessageSerializer<FindNodeMessage> serializer = new DiscoveryMessageSerializer<FindNodeMessage>(signer, _privateKey);
            FindNodeMessage initial = new FindNodeMessage {Port = 1, Host = "A"};
            initial.Payload = new byte[] {1, 2, 3};
            byte[] serialized = serializer.Serialize(initial);
            FindNodeMessage deserialized = serializer.Deserialize(serialized);
            Assert.AreEqual(initial.Payload, deserialized.Payload, "payload");
            Assert.AreEqual(initial.Signature, deserialized.Signature, "signature");
        }

        [Test]
        public void Neighbors_there_and_back()
        {
            ISigner signer = new Signer();
            DiscoveryMessageSerializer<NeighborsMessage> serializer = new DiscoveryMessageSerializer<NeighborsMessage>(signer, _privateKey);
            NeighborsMessage initial = new NeighborsMessage {Port = 1, Host = "A"};
            initial.Payload = new byte[] {1, 2, 3};
            byte[] serialized = serializer.Serialize(initial);
            NeighborsMessage deserialized = serializer.Deserialize(serialized);
            Assert.AreEqual(initial.Payload, deserialized.Payload, "payload");
            Assert.AreEqual(initial.Signature, deserialized.Signature, "signature");
        }

        [Test]
        public void Ping_there_and_back()
        {
            ISigner signer = new Signer();
            DiscoveryMessageSerializer<PingMessage> serializer = new DiscoveryMessageSerializer<PingMessage>(signer, _privateKey);
            PingMessage initial = new PingMessage {Port = 1, Host = "A"};
            byte[] serialized = serializer.Serialize(initial);
            PingMessage deserialized = serializer.Deserialize(serialized);
            Assert.AreEqual(initial.Payload, deserialized.Payload, "payload");
            Assert.AreEqual(initial.Signature, deserialized.Signature, "signature");
        }

        [Test]
        public void Pong_there_and_back()
        {
            ISigner signer = new Signer();
            DiscoveryMessageSerializer<PongMessage> serializer = new DiscoveryMessageSerializer<PongMessage>(signer, _privateKey);
            PongMessage initial = new PongMessage {Port = 1, Host = "A"};
            byte[] serialized = serializer.Serialize(initial);
            PongMessage deserialized = serializer.Deserialize(serialized);
            Assert.AreEqual(initial.Payload, deserialized.Payload, "payload");
            Assert.AreEqual(initial.Signature, deserialized.Signature, "signature");
        }
    }
}