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
        [TestCase("0x0")]
        [TestCase("0x12")]
        [TestCase("0x1234")]
        [TestCase("1")]
        [TestCase("123")]
        [TestCase("12345")]
        [TestCase("0")]
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

        [TestCase("0x0", 1)]
        [TestCase("0x12", 2)]
        [TestCase("0x1234", 4)]
        [TestCase("0", 1)]
        [TestCase("12", 2)]
        [TestCase("1234", 4)]
        public void Can_extract_nibbles(string hexString, int nibbleCount)
        {
            Nibble[] nibbles = Hex.ToNibbles(hexString);
            Assert.AreEqual(nibbleCount, nibbles.Length);
        }
    }
}