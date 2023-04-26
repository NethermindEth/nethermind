// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
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

        [Test]
        public void Nibbles_to_encoded_bytes_correct_output()
        {
            byte[] nibbles = Enumerable.Repeat((byte)1, 64).ToArray();
            byte[] bytes = Enumerable.Repeat((byte)17, 32).ToArray();
            byte[] result = Nibbles.ToEncodedStorageBytes(nibbles);

            //encoded / even
            Assert.AreEqual(0xfe, result[0]);
            CollectionAssert.AreEqual(bytes, result.AsSpan(1).ToArray());

            byte[] nibbles2 = Enumerable.Repeat((byte)1, 5).ToArray();
            var rawBytes = Enumerable.Repeat((byte)17, 2).ToList();
            rawBytes.Insert(0, 1);
            byte[] bytes2 = rawBytes.ToArray();

            byte[] result2 = Nibbles.ToEncodedStorageBytes(nibbles2);

            //encoded / odd
            Assert.AreEqual(0xff, result2[0]);
            CollectionAssert.AreEqual(bytes2, result2.AsSpan(1).ToArray());
        }

        [Test]
        public void Nibbles_encoding_decoding_for_path_based_tree()
        {
            byte[] nibbles = Enumerable.Repeat((byte)1, 64).ToArray();
            byte[] result = Nibbles.NibblesToByteStorage(nibbles);
            Nibbles.BytesToNibblesStorage(result).Should().BeEquivalentTo(nibbles);

            byte[] nibbles2 = Enumerable.Repeat((byte)1, 5).ToArray();
            byte[] result2 = Nibbles.NibblesToByteStorage(nibbles2);
            Nibbles.BytesToNibblesStorage(result2).Should().BeEquivalentTo(nibbles2);
        }

        [Test]
        public void Nibbles_to_byte_and_reverse()
        {
            List<byte[]> keys = new List<byte[]>
            {
                Bytes.FromHexString("1234"),
                KeccakHash.ComputeHash(TestItem.AddressA.Bytes).ToArray()
            };

            foreach (byte[]? key in keys)
            {
                Span<byte> nibbles =  new byte[2 * key.Length];

                Nibbles.BytesToNibbleBytes(key, nibbles);
                Nibbles.ToBytes(nibbles).Should().BeEquivalentTo(key);
            }
        }

        [Test]
        public void StoragePrefixTests()
        {
            byte[] storagePrefixBytes = KeccakHash.ComputeHash(TestItem.AddressA.Bytes).ToArray();
            Span<byte> storagePrefixNibbles= stackalloc byte[2 * storagePrefixBytes.Length];
            Nibbles.BytesToNibbleBytes(storagePrefixBytes, storagePrefixNibbles);
            Nibbles.ToBytes(storagePrefixNibbles).Should().BeEquivalentTo(storagePrefixBytes);

            List<byte[]> nodePaths = new List<byte[]>
            {
                Bytes.FromHexString("1234"),
                KeccakHash.ComputeHash(TestItem.AddressA.Bytes).ToArray(),
                KeccakHash.ComputeHash(TestItem.AddressB.Bytes).ToArray(),
                KeccakHash.ComputeHash(TestItem.AddressC.Bytes).ToArray(),
            };

            foreach (byte[]? nodePath in nodePaths)
            {
                byte[] nodeNibblePath = Nibbles.BytesToNibbleBytes(nodePath);

                byte[] storagePath = storagePrefixBytes.Concat(nodePath).ToArray();
                byte[] storageConcatNibblesPath = storagePrefixNibbles.ToArray().Concat(nodeNibblePath).ToArray();
                byte[] storageCalculatedNibblesPath = Nibbles.BytesToNibbleBytes(storagePath);

                storageCalculatedNibblesPath.Should().BeEquivalentTo(storageConcatNibblesPath);
                Nibbles.ToBytes(storageConcatNibblesPath).Should().BeEquivalentTo(storagePath);
            }
        }
    }
}
