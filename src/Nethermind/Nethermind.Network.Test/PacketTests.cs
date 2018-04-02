using Nethermind.Network.Rlpx;
using NUnit.Framework;

namespace Nethermind.Network.Test
{
    [TestFixture]
    public class PacketTests
    {
        [Test]
        public void Asggins_values_from_constructor()
        {
            byte[] data = {3, 4, 5};
            Packet packet = new Packet("eth", 2, data);
            Assert.AreEqual("eth", packet.ProtocolType);
            Assert.AreEqual(2, packet.PacketType);
            Assert.AreEqual(data, packet.Data);
        }
    }
}