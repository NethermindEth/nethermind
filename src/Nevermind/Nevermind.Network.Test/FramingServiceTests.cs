using NUnit.Framework;

namespace Nevermind.Network.Test
{
    [TestFixture]
    public class FramingServiceTests
    {
        [Test]
        public void Size_looks_ok()
        {
            FramingService framingService = new FramingService();
            byte[] packet = framingService.Package(0, 1, 0, new byte[1]);
            Assert.AreEqual(16 + 16 + 1 + 16 + 16, packet.Length);
        }

        [Test]
        public void Size_looks_ok_multiple_frames()
        {
            FramingService framingService = new FramingService();
            byte[] packet = framingService.Package(0, 1, 0, new byte[FramingService.MaxFrameSize + 1]);
            Assert.AreEqual(16 + 16 + 1 /* packet type */ + 1024 + 16 /* frame boundary */ + 16 + 16 + 16 /* padded */ + 16, packet.Length);
        }
    }
}