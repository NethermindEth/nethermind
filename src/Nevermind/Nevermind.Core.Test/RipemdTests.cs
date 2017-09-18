using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nevermind.Core.Encoding;

namespace Nevermind.Core.Test
{
    [TestClass]
    public class RipemdTests
    {
        public const string RipemdOfEmptyString = "9c1185a5c5e9fc54612808977ee8f548b2258d31";

        [TestMethod]
        public void Empty_byte_array()
        {
            string result = Ripemd.ComputeString(new byte[] { });
            Assert.AreEqual(RipemdOfEmptyString, result);
        }

        [TestMethod]
        public void Empty_string()
        {
            string result = Ripemd.ComputeString(string.Empty);
            Assert.AreEqual(RipemdOfEmptyString, result);
        }
    }
}