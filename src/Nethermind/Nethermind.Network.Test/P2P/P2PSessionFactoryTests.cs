using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    [TestFixture]
    public class P2PSessionFactoryTests
    {
        private const int ListenPort = 8002;

        [Test]
        public void Sets_listen_port()
        {
            P2PSessionFactory factory = new P2PSessionFactory(NetTestVectors.StaticKeyA.PublicKey, ListenPort);
            ISession session = factory.Create(Substitute.For<IMessageSender>());
            Assert.AreEqual(ListenPort, session.ListenPort);
        }

        [Test]
        public void Sets_local_node_id()
        {
            P2PSessionFactory factory = new P2PSessionFactory(NetTestVectors.StaticKeyA.PublicKey, ListenPort);
            ISession session = factory.Create(Substitute.For<IMessageSender>());
            Assert.AreEqual(NetTestVectors.StaticKeyA.PublicKey, session.LocalNodeId);
        }
    }
}