// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Merkleization;
using NUnit.Framework;
using Shouldly;

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
            Ssz.EncodeVector(encoded, input);

            // Assert
            string byteString = Bytes.ToHexString(encoded);
            byteString.ShouldBe(expectedByteString);
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
            decoded.ShouldBe(expected);
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
            hashTreeRoot.ToArray().ShouldBe(expectedHashTreeRoot);
        }

        [TestCaseSource(nameof(GetBitlistData))]
        public void Can_serialize_bitarray_bitlist(bool[] value, ulong limit, string expectedByteString, byte[] expectedHashTreeRoot)
        {
            // Arrange
            BitArray input = new(value);

            // Act
            byte[] encoded = new byte[(input.Length + 8) / 8];
            Ssz.EncodeList(encoded, input);

            // Assert
            string byteString = Bytes.ToHexString(encoded);
            byteString.ShouldBe(expectedByteString);
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
            decoded.ShouldBe(expected);
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
            hashTreeRoot.ToArray().ShouldBe(expectedHashTreeRoot);
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
                        HashUtility.Chunk(new byte[] { 0x01 }).ToArray(),
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
                    HashUtility.Chunk(new byte[] { 0x2b }).ToArray(),
                    HashUtility.Chunk(new byte[] { 0x08 }).ToArray()
                )
            ).SetName("Len8_Limit8");

            yield return new TestCaseData(
                new[] { false, true, false, true },
                (ulong)4,
                "1a",
                HashUtility.Hash(
                    HashUtility.Chunk(new byte[] { 0x0a }).ToArray(),
                    HashUtility.Chunk(new byte[] { 0x04 }).ToArray()
                )
            ).SetName("Len4_Limit4");

            yield return new TestCaseData(
                new[] { false, true, false },
                (ulong)3,
                "0a",
                HashUtility.Hash(
                    HashUtility.Chunk(new byte[] { 0x02 }).ToArray(),
                    HashUtility.Chunk(new byte[] { 0x03 }).ToArray()
                )
            ).SetName("Len3_Limit3");

            yield return new TestCaseData(
                new[] { true, false, true, false, false, false, true, true, false, true },
                (ulong)16,
                "c506",
                HashUtility.Hash(
                    HashUtility.Chunk(new byte[] { 0xc5, 0x02 }).ToArray(),
                    HashUtility.Chunk(new byte[] { 0x0a }).ToArray()
                )
            ).SetName("Len10_Limit16");

            yield return new TestCaseData(
                new[] { true, false, true, false, false, false, true, true,
                    false, true, false, false, false, false, true, true },
                (ulong)16,
                "c5c201",
                HashUtility.Hash(
                    HashUtility.Chunk(new byte[] { 0xc5, 0xc2 }).ToArray(),
                    HashUtility.Chunk(new byte[] { 0x10 }).ToArray()
                )
            ).SetName("Len16_Limit16");

            yield return new TestCaseData(
                new[] { true },
                (ulong)512,
                "03",
                HashUtility.Hash(
                    HashUtility.Hash(
                        HashUtility.Chunk(new byte[] { 0x01 }).ToArray(),
                        new byte[32]
                    ),
                    HashUtility.Chunk(new byte[] { 0x01 }).ToArray()
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
                    HashUtility.Chunk(new byte[] { 0x00, 0x02 }).ToArray()
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
                            HashUtility.Chunk(new byte[] { 0x01 }).ToArray(),
                            new byte[32]
                        )
                    ),
                    HashUtility.Chunk(new byte[] { 0x01, 0x02 }).ToArray()
                )
            ).SetName("Len513_Limit513");
        }

    }
}
