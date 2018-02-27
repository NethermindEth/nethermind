using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class Keccak512Tests
    {
        [Test]
        public void Empty_string()
        {
            string result = Keccak512.Compute(string.Empty).ToString();
            Assert.AreEqual(Keccak512.OfAnEmptyString.ToString(), result);
        }

        [Test]
        public void Null_string()
        {
            string result = Keccak512.Compute((string)null).ToString();
            Assert.AreEqual(Keccak512.OfAnEmptyString.ToString(), result);
        }

        [Test]
        public void Null_bytes()
        {
            string result = Keccak512.Compute((byte[])null).ToString();
            Assert.AreEqual(Keccak512.OfAnEmptyString.ToString(), result);
        }

        [Test]
        public void Zero()
        {
            string result = Keccak512.Zero.ToString();
            Assert.AreEqual("0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000", result);
        }
    }
}