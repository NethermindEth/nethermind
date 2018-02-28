using Nethermind.Network.P2P;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    [TestFixture]
    public class P2PSessionTests
    {
        [SetUp]
        public void Setup()
        {
            _messageSender = Substitute.For<IMessageSender>();
        }

        private IMessageSender _messageSender;

        [Test]
        public void Can_ping()
        {
            P2PSession session = new P2PSession(_messageSender, NetTestVectors.StaticKeyA.PublicKey, 8002);
            session.Ping();
            _messageSender.Received(1).Enqueue(Arg.Any<PingMessage>());
        }

        [Test]
        public void On_init_outbound_sends_a_hello_message()
        {
            P2PSession session = new P2PSession(_messageSender, NetTestVectors.StaticKeyA.PublicKey, 8002);
            session.InitOutbound();

            _messageSender.Received(1).Enqueue(Arg.Any<HelloMessage>());
        }

        [Test]
        public void Pongs_to_ping()
        {
            P2PSession session = new P2PSession(_messageSender, NetTestVectors.StaticKeyA.PublicKey, 8002);
            session.HandlePing();
            _messageSender.Received(1).Enqueue(Arg.Any<PongMessage>());
        }

        [Test]
        public void Sets_local_node_id_from_constructor()
        {
            P2PSession session = new P2PSession(_messageSender, NetTestVectors.StaticKeyA.PublicKey, 8002);
            Assert.AreEqual(session.LocalNodeId, NetTestVectors.StaticKeyA.PublicKey);
        }

        [Test]
        public void Sets_port_from_constructor()
        {
            P2PSession session = new P2PSession(_messageSender, NetTestVectors.StaticKeyA.PublicKey, 8002);
            Assert.AreEqual(session.ListenPort, 8002);
        }
    }
}