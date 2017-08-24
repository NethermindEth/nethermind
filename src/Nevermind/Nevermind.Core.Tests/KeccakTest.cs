using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Nevermind.Core.Tests
{
    [TestClass]
    public class KeccakTest
    {
        [TestMethod]
        public void Keccak_of_empty_byte_array()
        {
            string result = Keccak.ComputeString(new byte[] {});
            Assert.AreEqual(result, "c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470");
        }

        [TestMethod]
        public void Keccak_of_empty_string()
        {
            string result = Keccak.ComputeString(string.Empty);
            Assert.AreEqual(result, "c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470");
        }
    }
}
