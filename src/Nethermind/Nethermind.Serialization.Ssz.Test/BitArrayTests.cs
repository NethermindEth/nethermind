// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;
using NUnit.Framework;

namespace Nethermind.Serialization.Ssz.Test
{
    [TestFixture]
    public class BitArrayTests
    {
        [TestCaseSource(nameof(GetBitvectorData))]
        public void Can_serialize_bitarray_bitvector(bool[] value, string expectedByteString, byte[] expectedHashTreeRoot)
        {
            // Arrange
            BitArray input = new(value);

            // Act
            byte[] encoded = new byte[(input.Length + 7) / 8];
            Ssz.Encode(encoded, input);

            // Assert
            string byteString = Bytes.ToHexString(encoded);
            Assert.That(byteString, Is.EqualTo(expectedByteString));
        }

        [TestCaseSource(nameof(GetBitvectorData))]
        public void Can_deserialize_bitarray_bitvector(bool[] value, string expectedByteString, byte[] expectedHashTreeRoot)
        {
            // Arrange
            byte[] encoded = Bytes.FromHexString(expectedByteString);
            int vectorLength = value.Length;

            // Act
            BitArray decoded = Ssz.DecodeBitvector(encoded, vectorLength);

            // Assert
            BitArray expected = new(value);
            Assert.That(decoded, Is.EqualTo(expected));
        }

        [TestCaseSource(nameof(GetBitvectorData))]
        public void Can_merkleize_bitarray_bitvector(bool[] value, string expectedByteString, byte[] expectedHashTreeRoot)
        {
            // Arrange
            BitArray input = new(value);

            // Act
            byte[] hashTreeRoot = new byte[32];
            Merkle.Merkleize(out UInt256 root, input);
            root.ToLittleEndian(hashTreeRoot);

            // Assert
            Assert.That(hashTreeRoot.ToArray(), Is.EqualTo(expectedHashTreeRoot));
        }

        [TestCaseSource(nameof(GetBitlistData))]
        public void Can_serialize_bitarray_bitlist(bool[] value, ulong limit, string expectedByteString, byte[] expectedHashTreeRoot)
        {
            // Arrange
            BitArray input = new(value);

            // Act
            byte[] encoded = new byte[(input.Length + 8) / 8];
            Ssz.Encode(encoded, input, (int)limit);

            // Assert
            string byteString = Bytes.ToHexString(encoded);
            Assert.That(byteString, Is.EqualTo(expectedByteString));
        }

        [TestCaseSource(nameof(GetBitlistData))]
        public void Can_deserialize_bitarray_bitlist(bool[] value, ulong limit, string expectedByteString, byte[] expectedHashTreeRoot)
        {
            // Arrange
            byte[] encoded = Bytes.FromHexString(expectedByteString);

            // Act
            BitArray decoded = Ssz.DecodeBitlist(encoded);

            // Assert
            BitArray expected = new(value);
            Assert.That(decoded, Is.EqualTo(expected));
        }

        [TestCaseSource(nameof(GetBitlistData))]
        public void Can_merkleize_bitarray_bitlist(bool[] value, ulong maximumBitlistLength, string expectedByteString, byte[] expectedHashTreeRoot)
        {
            // Arrange
            BitArray input = new(value);

            // Act
            byte[] hashTreeRoot = new byte[32];
            Merkle.Merkleize(out UInt256 root, input, maximumBitlistLength);
            root.ToLittleEndian(hashTreeRoot);

            // Assert
            Assert.That(hashTreeRoot.ToArray(), Is.EqualTo(expectedHashTreeRoot));
        }

        [Test]
        public void Merkleize_bitlist_rejects_bits_above_limit([Values(0UL, 1UL, 255UL, 256UL)] ulong limit)
        {
            BitArray bits = new((int)limit + 1);

            Assert.That(() => Merkle.Merkleize(out _, bits, limit), Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        [Test]
        public void Merkleize_bitlist_supports_full_ulong_limit()
        {
            BitArray bits = new(new[] { true });
            UInt256 subtree = MerkleTests.ReferenceRoot(new byte[] { 1 }, 1UL << 56);
            UInt256 expected = new(HashUtility.Hash(subtree.ToLittleEndian(), HashUtility.Chunk(new byte[] { 1 })));

            Merkle.Merkleize(out UInt256 actual, bits, ulong.MaxValue);

            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void Merkleize_ignores_unused_bits_without_modifying_input(
            [Values(1, 7, 8, 9, 255, 256, 257, 1279, 1280, 1281, 8388607)] int bitLength,
            [Values] bool progressive)
        {
            BitArray bits = new(bitLength);
            Span<byte> storage = CollectionsMarshal.AsBytes(bits);
            storage.Fill(0xFF);
            byte[] packed = storage.ToArray();
            if (bitLength % 8 != 0)
            {
                packed[^1] &= (byte)((1 << (bitLength % 8)) - 1);
            }

            UInt256 expected = progressive ? MerkleTests.ReferenceProgressiveRoot(packed) : MerkleTests.ReferenceRoot(packed);
            UInt256 actual;
            if (progressive)
            {
                expected = new UInt256(HashUtility.Hash(expected.ToLittleEndian(), new UInt256((uint)bitLength).ToLittleEndian()));
                Merkle.MerkleizeProgressive(out actual, bits);
            }
            else
            {
                Merkle.Merkleize(out actual, bits);
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(actual, Is.EqualTo(expected));
                Assert.That(CollectionsMarshal.AsBytes(bits).ToArray(), Is.All.EqualTo(0xFF));
            }
        }

        public static IEnumerable<TestCaseData> GetBitvectorData()
        {
            yield return new TestCaseData(
                new[] { true, true, false, true, false, true, false, false },
                "2b",
                HashUtility.Chunk(new byte[] { 0x2b }).ToArray()
            ).SetName("Len8_4True");

            yield return new TestCaseData(
                new[] { false, true, false, true },
                "0a",
                HashUtility.Chunk(new byte[] { 0x0a }).ToArray()
            ).SetName("Len4_2True");

            yield return new TestCaseData(
                new[] { false, true, false },
                "02",
                HashUtility.Chunk(new byte[] { 0x02 }).ToArray()
            ).SetName("Len3_1True");

            yield return new TestCaseData(
                new[] { true, false, true, false, false, false, true, true, false, true },
                "c502",
                HashUtility.Chunk(new byte[] { 0xc5, 0x02 }).ToArray()
            ).SetName("Len10_5True");

            yield return new TestCaseData(
                new[] { true, false, true, false, false, false, true, true,
                    false, true, false, false, false, false, true, true },
                "c5c2",
                HashUtility.Chunk(new byte[] { 0xc5, 0xc2 }).ToArray()
            ).SetName("Len16_7True");

            yield return new TestCaseData(
                Enumerable.Repeat(true, 512).ToArray(),
                new string('f', 64 * 2),
                HashUtility.Hash(
                    Enumerable.Repeat((byte)0xff, 32).ToArray(),
                    Enumerable.Repeat((byte)0xff, 32).ToArray()
                )
            ).SetName("Len512_512True");

            yield return new TestCaseData(
                Enumerable.Repeat(true, 513).ToArray(),
                new string('f', 64 * 2) + "01",
                HashUtility.Hash(
                    HashUtility.Hash(
                        Enumerable.Repeat((byte)0xff, 32).ToArray(),
                        Enumerable.Repeat((byte)0xff, 32).ToArray()
                    ),
                    HashUtility.Hash(
                        HashUtility.Chunk(new byte[] { 0x01 }),
                        new byte[32]
                    )
                )
            ).SetName("Len513_513True");
        }

        public static IEnumerable<TestCaseData> GetBitlistData()
        {
            yield return new TestCaseData(
                new[] { true, true, false, true, false, true, false, false },
                (ulong)8,
                "2b01",
                HashUtility.Hash(
                    HashUtility.Chunk(new byte[] { 0x2b }),
                    HashUtility.Chunk(new byte[] { 0x08 })
                )
            ).SetName("Len8_Limit8");

            yield return new TestCaseData(
                new[] { false, true, false, true },
                (ulong)4,
                "1a",
                HashUtility.Hash(
                    HashUtility.Chunk(new byte[] { 0x0a }),
                    HashUtility.Chunk(new byte[] { 0x04 })
                )
            ).SetName("Len4_Limit4");

            yield return new TestCaseData(
                new[] { false, true, false },
                (ulong)3,
                "0a",
                HashUtility.Hash(
                    HashUtility.Chunk(new byte[] { 0x02 }),
                    HashUtility.Chunk(new byte[] { 0x03 })
                )
            ).SetName("Len3_Limit3");

            yield return new TestCaseData(
                new[] { true, false, true, false, false, false, true, true, false, true },
                (ulong)16,
                "c506",
                HashUtility.Hash(
                    HashUtility.Chunk(new byte[] { 0xc5, 0x02 }),
                    HashUtility.Chunk(new byte[] { 0x0a })
                )
            ).SetName("Len10_Limit16");

            yield return new TestCaseData(
                new[] { true, false, true, false, false, false, true, true,
                    false, true, false, false, false, false, true, true },
                (ulong)16,
                "c5c201",
                HashUtility.Hash(
                    HashUtility.Chunk(new byte[] { 0xc5, 0xc2 }),
                    HashUtility.Chunk(new byte[] { 0x10 })
                )
            ).SetName("Len16_Limit16");

            yield return new TestCaseData(
                new[] { true },
                (ulong)512,
                "03",
                HashUtility.Hash(
                    HashUtility.Hash(
                        HashUtility.Chunk(new byte[] { 0x01 }),
                        new byte[32]
                    ),
                    HashUtility.Chunk(new byte[] { 0x01 })
                )
            ).SetName("Len1_Limit512");

            yield return new TestCaseData(
                Enumerable.Repeat(true, 512).ToArray(),
                (ulong)512,
                new string('f', 64 * 2) + "01",
                HashUtility.Hash(
                    HashUtility.Hash(
                        Enumerable.Repeat((byte)0xff, 32).ToArray(),
                        Enumerable.Repeat((byte)0xff, 32).ToArray()
                    ),
                    HashUtility.Chunk(new byte[] { 0x00, 0x02 })
                )
            ).SetName("Len512_Limit512");

            yield return new TestCaseData(
                Enumerable.Repeat(true, 513).ToArray(),
                (ulong)513,
                new string('f', 64 * 2) + "03",
                HashUtility.Hash(
                    HashUtility.Hash(
                        HashUtility.Hash(
                            Enumerable.Repeat((byte)0xff, 32).ToArray(),
                            Enumerable.Repeat((byte)0xff, 32).ToArray()
                        ),
                        HashUtility.Hash(
                            HashUtility.Chunk(new byte[] { 0x01 }),
                            new byte[32]
                        )
                    ),
                    HashUtility.Chunk(new byte[] { 0x01, 0x02 })
                )
            ).SetName("Len513_Limit513");
        }

    }
}
