using Nevermind.Core.Encoding;
using NUnit.Framework;

namespace Nevermind.Core.Test
{
    [TestFixture]
    public class Sha2Tests
    {
        public const string Sha2OfEmptyString = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        [Test]
        public void Empty_byte_array()
        {
            string result = Sha2.ComputeString(new byte[] { });
            Assert.AreEqual(Sha2OfEmptyString, result);
        }

        [Test]
        public void Empty_string()
        {
            string result = Sha2.ComputeString(string.Empty);
            Assert.AreEqual(Sha2OfEmptyString, result);
        }
    }
}