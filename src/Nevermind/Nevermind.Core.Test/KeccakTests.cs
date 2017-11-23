using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;
using NUnit.Framework;

namespace Nevermind.Core.Test
{
    [TestFixture]
    public class KeccakTests
    {
        public const string KeccakOfAnEmptyString = "0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470";
        public const string KeccakZero = "0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470";

        [Test]
        public void Empty_byte_array()
        {
            string result = Keccak.Compute(new byte[] { }).ToString();
            Assert.AreEqual(KeccakOfAnEmptyString, result);
        }

        [Test]
        public void Empty_string()
        {
            string result = Keccak.Compute(string.Empty).ToString();
            Assert.AreEqual(KeccakOfAnEmptyString, result);
        }

        [Test]
        public void Null_string()
        {
            string result = Keccak.Compute((string)null).ToString();
            Assert.AreEqual(KeccakOfAnEmptyString, result);
        }

        [Test]
        public void Null_bytes()
        {
            string result = Keccak.Compute((byte[])null).ToString();
            Assert.AreEqual(KeccakOfAnEmptyString, result);
        }

        [Test]
        public void Zero()
        {
            string result = Keccak.Zero.ToString();
            Assert.AreEqual("0x0000000000000000000000000000000000000000000000000000000000000000", result);
        }
    }
}