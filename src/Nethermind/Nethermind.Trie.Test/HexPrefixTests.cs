// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using NUnit.Framework;

namespace Nethermind.Trie.Test
{
    [TestFixture]
    public class HexPrefixTests
    {
        [TestCase(false, (byte)3, (byte)19)]
        [TestCase(true, (byte)3, (byte)51)]
        public void Encode_gives_correct_output_when_one(bool flag, byte nibble1, byte byte1)
        {
            byte[] output = HexPrefix.ToBytes(new[] { nibble1 }, flag);
            Assert.That(output.Length, Is.EqualTo(1));
            Assert.That(output[0], Is.EqualTo(byte1));
        }

        [TestCase(false, (byte)3, (byte)7, (byte)13, (byte)19, (byte)125)]
        [TestCase(true, (byte)3, (byte)7, (byte)13, (byte)51, (byte)125)]
        public void Encode_gives_correct_output_when_odd(bool flag, byte nibble1, byte nibble2, byte nibble3,
            byte byte1, byte byte2)
        {
            byte[] output = HexPrefix.ToBytes(new[] { nibble1, nibble2, nibble3 }, flag);

            Assert.That(output.Length, Is.EqualTo(2));
            Assert.That(output[0], Is.EqualTo(byte1));
            Assert.That(output[1], Is.EqualTo(byte2));
        }

        [TestCase(false, (byte)3, (byte)7, (byte)0, (byte)55)]
        [TestCase(true, (byte)3, (byte)7, (byte)32, (byte)55)]
        public void Encode_gives_correct_output_when_even(bool flag, byte nibble1, byte nibble2, byte byte1, byte byte2)
        {
            byte[] output = HexPrefix.ToBytes(new[] { nibble1, nibble2 }, flag);

            Assert.That(output.Length, Is.EqualTo(2));
            Assert.That(output[0], Is.EqualTo(byte1));
            Assert.That(output[1], Is.EqualTo(byte2));
        }

        [TestCase(false, (byte)3, (byte)7, (byte)0, (byte)55)]
        [TestCase(true, (byte)3, (byte)7, (byte)32, (byte)55)]
        public void Decode_gives_correct_output_when_even(bool expectedFlag, byte nibble1, byte nibble2, byte byte1,
            byte byte2)
        {
            (byte[] key, bool isLeaf) = HexPrefix.FromBytes(new[] { byte1, byte2 });
            Assert.That(isLeaf, Is.EqualTo(expectedFlag));
            Assert.That(key.Length, Is.EqualTo(2));
            Assert.That(key[0], Is.EqualTo(nibble1));
            Assert.That(key[1], Is.EqualTo(nibble2));
        }

        [TestCase(false, (byte)3, (byte)19)]
        [TestCase(true, (byte)3, (byte)51)]
        public void Decode_gives_correct_output_when_one(bool expectedFlag, byte nibble1, byte byte1)
        {
            (byte[] key, bool isLeaf) = HexPrefix.FromBytes(new[] { byte1 });

            Assert.That(isLeaf, Is.EqualTo(expectedFlag));
            Assert.That(key.Length, Is.EqualTo(1));
            Assert.That(key[0], Is.EqualTo(nibble1));
        }

        [TestCase(false, (byte)3, (byte)7, (byte)13, (byte)19, (byte)125)]
        [TestCase(true, (byte)3, (byte)7, (byte)13, (byte)51, (byte)125)]
        public void Decode_gives_correct_output_when_odd(bool expectedFlag, byte nibble1, byte nibble2, byte nibble3,
            byte byte1, byte byte2)
        {
            (byte[] key, bool isLeaf) = HexPrefix.FromBytes(new[] { byte1, byte2 });
            Assert.That(isLeaf, Is.EqualTo(expectedFlag));
            Assert.That(key.Length, Is.EqualTo(3));
            Assert.That(key[0], Is.EqualTo(nibble1));
            Assert.That(key[1], Is.EqualTo(nibble2));
            Assert.That(key[2], Is.EqualTo(nibble3));
        }

        // According to: https://ethereum.github.io/yellowpaper/paper.pdf#appendix.C
        // Leaf flag (t) is omitted
        [TestCase(new byte[] { 1, 2, 3, 4 }, new byte[] { 0, 1 * 16 + 2, 3 * 16 + 4 })]
        [TestCase(new byte[] { 1, 2, 3 }, new byte[] { 16 + 1, 2 * 16 + 3 })]
        public void Compact_hex_encoding_correct_output(byte[] nibbles, byte[] bytes)
        {
            byte[] result = Nibbles.ToCompactHexEncoding(nibbles);
            CollectionAssert.AreEqual(bytes, result);
        }

        // Just pack nibbles to bytes
        [Test]
        public void Nibbles_to_bytes_correct_output()
        {
            byte[] nibbles = Enumerable.Repeat((byte)1, 64).ToArray();
            byte[] bytes = Enumerable.Repeat((byte)17, 32).ToArray();
            byte[] result = Nibbles.ToBytes(nibbles);
            CollectionAssert.AreEqual(bytes, result);
        }
    }
}
