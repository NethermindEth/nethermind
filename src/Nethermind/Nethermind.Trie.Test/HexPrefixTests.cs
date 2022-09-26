//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
            Assert.AreEqual(1, output.Length);
            Assert.AreEqual(byte1, output[0]);
        }

        [TestCase(false, (byte)3, (byte)7, (byte)13, (byte)19, (byte)125)]
        [TestCase(true, (byte)3, (byte)7, (byte)13, (byte)51, (byte)125)]
        public void Encode_gives_correct_output_when_odd(bool flag, byte nibble1, byte nibble2, byte nibble3,
            byte byte1, byte byte2)
        {
            byte[] output = HexPrefix.ToBytes(new[] { nibble1, nibble2, nibble3 }, flag);

            Assert.AreEqual(2, output.Length);
            Assert.AreEqual(byte1, output[0]);
            Assert.AreEqual(byte2, output[1]);
        }

        [TestCase(false, (byte)3, (byte)7, (byte)0, (byte)55)]
        [TestCase(true, (byte)3, (byte)7, (byte)32, (byte)55)]
        public void Encode_gives_correct_output_when_even(bool flag, byte nibble1, byte nibble2, byte byte1, byte byte2)
        {
            byte[] output = HexPrefix.ToBytes(new[] { nibble1, nibble2 }, flag);

            Assert.AreEqual(2, output.Length);
            Assert.AreEqual(byte1, output[0]);
            Assert.AreEqual(byte2, output[1]);
        }

        [TestCase(false, (byte)3, (byte)7, (byte)0, (byte)55)]
        [TestCase(true, (byte)3, (byte)7, (byte)32, (byte)55)]
        public void Decode_gives_correct_output_when_even(bool expectedFlag, byte nibble1, byte nibble2, byte byte1,
            byte byte2)
        {
            (byte[] key, bool isLeaf) = HexPrefix.FromBytes(new[] { byte1, byte2 });
            Assert.AreEqual(expectedFlag, isLeaf);
            Assert.AreEqual(2, key.Length);
            Assert.AreEqual(nibble1, key[0]);
            Assert.AreEqual(nibble2, key[1]);
        }

        [TestCase(false, (byte)3, (byte)19)]
        [TestCase(true, (byte)3, (byte)51)]
        public void Decode_gives_correct_output_when_one(bool expectedFlag, byte nibble1, byte byte1)
        {
            (byte[] key, bool isLeaf) = HexPrefix.FromBytes(new[] { byte1 });

            Assert.AreEqual(expectedFlag, isLeaf);
            Assert.AreEqual(1, key.Length);
            Assert.AreEqual(nibble1, key[0]);
        }

        [TestCase(false, (byte)3, (byte)7, (byte)13, (byte)19, (byte)125)]
        [TestCase(true, (byte)3, (byte)7, (byte)13, (byte)51, (byte)125)]
        public void Decode_gives_correct_output_when_odd(bool expectedFlag, byte nibble1, byte nibble2, byte nibble3,
            byte byte1, byte byte2)
        {
            (byte[] key, bool isLeaf) = HexPrefix.FromBytes(new[] { byte1, byte2 });
            Assert.AreEqual(expectedFlag, isLeaf);
            Assert.AreEqual(3, key.Length);
            Assert.AreEqual(nibble1, key[0]);
            Assert.AreEqual(nibble2, key[1]);
            Assert.AreEqual(nibble3, key[2]);
        }
    }
}
