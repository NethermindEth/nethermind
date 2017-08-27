using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Nevermind.Core.Test
{
    [TestClass]
    public class Sha3Tests
    {
        private const string Sha3OfEmptyString = "a7ffc6f8bf1ed76651c14756a061d662f580ff4de43b49fa82d80a4b80f8434a";

        [TestMethod]
        public void Sha3_of_empty_byte_array()
        {
            string result = Sha3.ComputeString(new byte[] {});
            Assert.AreEqual(result, Sha3OfEmptyString);
        }

        [TestMethod]
        public void Keccak_of_empty_string()
        {
            string result = Sha3.ComputeString(string.Empty);
            Assert.AreEqual(result, Sha3OfEmptyString);
        }
    }
}
