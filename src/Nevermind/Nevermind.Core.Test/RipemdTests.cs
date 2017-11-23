using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;
using NUnit.Framework;

namespace Nevermind.Core.Test
{
    [TestFixture]
    public class RipemdTests
    {
        public const string RipemdOfEmptyString = "9c1185a5c5e9fc54612808977ee8f548b2258d31";

        [Test]
        public void Empty_byte_array()
        {
            string result = Ripemd.ComputeString(new byte[] { });
            Assert.AreEqual(RipemdOfEmptyString, result);
        }

        [Test]
        public void Empty_string()
        {
            string result = Ripemd.ComputeString(string.Empty);
            Assert.AreEqual(RipemdOfEmptyString, result);
        }
    }
}