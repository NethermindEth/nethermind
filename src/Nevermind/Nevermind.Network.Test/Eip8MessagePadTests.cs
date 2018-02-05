using System;
using Nevermind.Core.Crypto;
using NUnit.Framework;

namespace Nevermind.Network.Test
{
    [TestFixture]
    public class Eip8MessagePadTests
    {
        private class TestRandom : ICryptoRandom
        {
            private readonly Func<int, int> _nextIntFunc;

            public TestRandom(Func<int, int> nextIntFunc)
            {
                _nextIntFunc = nextIntFunc;
            }

            public byte[] GenerateRandomBytes(int length)
            {
                return new byte[length];
            }

            public int NextInt(int max)
            {
                return _nextIntFunc(max);
            }
        }

        [Test]
        public void Adds_at_least_100_bytes()
        {
            byte[] message = {1};
            int lengthBeforePadding = message.Length;

            ICryptoRandom testRandom = new TestRandom(i => 0);
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

            ICryptoRandom testRandom = new TestRandom(i => i - 1);
            Eip8MessagePad pad = new Eip8MessagePad(testRandom);
            message = pad.Pad(message);

            Assert.AreEqual(lengthBeforePadding + 300, message.Length, "incorrect length");
            Assert.AreEqual(message[0], 1, "first byte touched");
        }
    }
}