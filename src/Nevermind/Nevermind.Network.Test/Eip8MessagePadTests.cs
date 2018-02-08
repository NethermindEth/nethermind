using NUnit.Framework;

namespace Nevermind.Network.Test
{
    [TestFixture]
    public class Eip8MessagePadTests
    {
        [Test]
        public void Adds_at_least_100_bytes()
        {
            byte[] message = {1};
            int lengthBeforePadding = message.Length;

            TestRandom testRandom = new TestRandom(i => 0, i => new byte[i]);

            Eip8MessagePad pad = new Eip8MessagePad(testRandom);
            message = pad.Pad(message);

            Assert.AreEqual(lengthBeforePadding + 100, message.Length, "incorrect length");
            Assert.AreEqual(message[0], 1, "first byte touched");
        }

        [Test]
        public void Adds_at_most_300_bytes()
        {
            byte[] message = {1};
            int lengthBeforePadding = message.Length;

            TestRandom testRandom = new TestRandom(i => i - 1, i => new byte[i]);

            Eip8MessagePad pad = new Eip8MessagePad(testRandom);
            message = pad.Pad(message);

            Assert.AreEqual(lengthBeforePadding + 300, message.Length, "incorrect length");
            Assert.AreEqual(message[0], 1, "first byte touched");
        }
    }
}