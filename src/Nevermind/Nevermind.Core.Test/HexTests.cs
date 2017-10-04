using Nevermind.Core.Encoding;
using NUnit.Framework;

namespace Nevermind.Core.Test
{
    [TestFixture]
    public class HexTests
    {
        [TestCase("0x1")]
        [TestCase("0x123")]
        [TestCase("0x12345")]
        [TestCase("0x")]
        [TestCase("0x12")]
        [TestCase("1234")]
        [TestCase("1")]
        [TestCase("123")]
        [TestCase("12345")]
        [TestCase("")]
        [TestCase("12")]
        [TestCase("1234")]
        public void Can_convert_from_string_and_back_when_leading_zeros_are_missing(string hexString)
        {
            bool withZeroX = hexString.StartsWith("0x");
            Hex hex = new Hex(hexString);
            Assert.AreEqual(hexString, hex.ToString(withZeroX, true), $"held as {nameof(Hex)} from string");

            byte[] bytes = Hex.ToBytes(hexString);
            string result = Hex.FromBytes(bytes, withZeroX, true);
            Assert.AreEqual(hexString, result, "converted twice");

            hex = new Hex(bytes);
            Assert.AreEqual(hexString, hex.ToString(withZeroX, true), $"held as {nameof(Hex)} from bytes");
        }
    }
}