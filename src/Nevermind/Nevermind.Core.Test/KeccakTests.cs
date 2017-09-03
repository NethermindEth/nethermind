using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Nevermind.Core.Test
{
    [TestClass]
    public class KeccakTests
    {
        public const string KeccakOfEmptyString = "c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470";

        [TestMethod]
        public void Empty_byte_array()
        {
            string result = Keccak.ComputeString(new byte[] { });
            Assert.AreEqual(KeccakOfEmptyString, result);
        }

        [TestMethod]
        public void Empty_string()
        {
            string result = Keccak.ComputeString(string.Empty);
            Assert.AreEqual(KeccakOfEmptyString, result);
        }
    }
}