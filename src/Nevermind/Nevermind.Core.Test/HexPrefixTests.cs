using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nevermind.Core.Encoding;

namespace Nevermind.Core.Test
{
    [TestClass]
    public class HexPrefixTests
    {
        [DataTestMethod]
        [DataRow(false, (byte)3, (byte)19)]
        [DataRow(true, (byte)3, (byte)51)]
        public void Encode_gives_correct_output_when_one(bool flag, byte nibble1, byte byte1)
        {
            HexPrefix hexPrefix = new HexPrefix(flag, nibble1);
            byte[] output = hexPrefix.ToBytes();
            Assert.AreEqual(1, output.Length);
            Assert.AreEqual(byte1, output[0]);
        }

        [DataTestMethod]
        [DataRow(false, (byte)3, (byte)7, (byte)13, (byte)19, (byte)125)]
        [DataRow(true, (byte)3, (byte)7, (byte)13, (byte)51, (byte)125)]
        public void Encode_gives_correct_output_when_odd(bool flag, byte nibble1, byte nibble2, byte nibble3, byte byte1, byte byte2)
        {
            HexPrefix hexPrefix = new HexPrefix(flag, nibble1, nibble2, nibble3);
            byte[] output = hexPrefix.ToBytes();
            Assert.AreEqual(2, output.Length);
            Assert.AreEqual(byte1, output[0]);
            Assert.AreEqual(byte2, output[1]);
        }

        [DataTestMethod]
        [DataRow(false, (byte)3, (byte)7, (byte)0, (byte)55)]
        [DataRow(true, (byte)3, (byte)7, (byte)32, (byte)55)]
        public void Encode_gives_correct_output_when_even(bool flag, byte nibble1, byte nibble2, byte byte1, byte byte2)
        {
            HexPrefix hexPrefix = new HexPrefix(flag, nibble1, nibble2);
            byte[] output = hexPrefix.ToBytes();
            Assert.AreEqual(2, output.Length);
            Assert.AreEqual(byte1, output[0]);
            Assert.AreEqual(byte2, output[1]);
        }

        [DataTestMethod]
        [DataRow(false, (byte)3, (byte)7, (byte)0, (byte)55)]
        [DataRow(true, (byte)3, (byte)7, (byte)32, (byte)55)]
        public void Decode_gives_correct_output_when_even(bool expectedFlag, byte nibble1, byte nibble2, byte byte1, byte byte2)
        {
            HexPrefix hexPrefix = HexPrefix.FromBytes(new[] { byte1, byte2 });
            Assert.AreEqual(expectedFlag, hexPrefix.Flag);
            Assert.AreEqual(2, hexPrefix.Nibbles.Length);
            Assert.AreEqual(nibble1, hexPrefix.Nibbles[0]);
            Assert.AreEqual(nibble2, hexPrefix.Nibbles[1]);
        }

        [DataTestMethod]
        [DataRow(false, (byte)3, (byte)19)]
        [DataRow(true, (byte)3, (byte)51)]
        public void Decode_gives_correct_output_when_one(bool expectedFlag, byte nibble1, byte byte1)
        {
            HexPrefix hexPrefix = HexPrefix.FromBytes(new[] { byte1 });
            Assert.AreEqual(expectedFlag, hexPrefix.Flag);
            Assert.AreEqual(1, hexPrefix.Nibbles.Length);
            Assert.AreEqual(nibble1, hexPrefix.Nibbles[0]);
        }

        [DataTestMethod]
        [DataRow(false, (byte)3, (byte)7, (byte)13, (byte)19, (byte)125)]
        [DataRow(true, (byte)3, (byte)7, (byte)13, (byte)51, (byte)125)]
        public void Decode_gives_correct_output_when_odd(bool expectedFlag, byte nibble1, byte nibble2, byte nibble3, byte byte1, byte byte2)
        {
            HexPrefix hexPrefix = HexPrefix.FromBytes(new[] { byte1, byte2 });
            Assert.AreEqual(expectedFlag, hexPrefix.Flag);
            Assert.AreEqual(3, hexPrefix.Nibbles.Length);
            Assert.AreEqual(nibble1, hexPrefix.Nibbles[0]);
            Assert.AreEqual(nibble2, hexPrefix.Nibbles[1]);
            Assert.AreEqual(nibble3, hexPrefix.Nibbles[2]);
        }
    }
}
