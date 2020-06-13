//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core2;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Merkleization;
using NUnit.Framework;
using Shouldly;

namespace Nethermind.Ssz.Test
{
    [TestFixture]
    public class BitArrayTests
    {
        [TestCaseSource(nameof(GetBitvectorData))]
        public void Can_serialize_bitarray_bitvector(bool[] value, string expectedByteString, byte[] expectedHashTreeRoot)
        {
            // Arrange
            var input = new BitArray(value);

            // Act
            var encoded = new byte[(input.Length + 7) / 8];
            Ssz.EncodeVector(encoded, input);

            // Assert
            var byteString = Bytes.ToHexString(encoded);
            byteString.ShouldBe(expectedByteString);
        }

        [TestCaseSource(nameof(GetBitvectorData))]
        public void Can_deserialize_bitarray_bitvector(bool[] value, string expectedByteString, byte[] expectedHashTreeRoot)
        {
            // Arrange
            var encoded = Bytes.FromHexString(expectedByteString);
            var vectorLength = value.Length;

            // Act
            BitArray decoded = Ssz.DecodeBitvector(encoded, vectorLength);

            // Assert
            BitArray expected = new BitArray(value);
            decoded.ShouldBe(expected);
        }

        [TestCaseSource(nameof(GetBitvectorData))]
        public void Can_merkleize_bitarray_bitvector(bool[] value, string expectedByteString, byte[] expectedHashTreeRoot)
        {
            // Arrange
            var input = new BitArray(value);

            // Act
            var hashTreeRoot = new byte[32];
            Merkle.IzeBitvector(out UInt256 root, input);
            root.ToLittleEndian(hashTreeRoot);

            // Assert
            hashTreeRoot.ToArray().ShouldBe(expectedHashTreeRoot);
        }

        [TestCaseSource(nameof(GetBitlistData))]
        public void Can_serialize_bitarray_bitlist(bool[] value, ulong limit, string expectedByteString, byte[] expectedHashTreeRoot)
        {
            // Arrange
            var input = new BitArray(value);

            // Act
            var encoded = new byte[(input.Length + 8) / 8];
            Ssz.EncodeList(encoded, input);

            // Assert
            var byteString = Bytes.ToHexString(encoded);
            byteString.ShouldBe(expectedByteString);
        }

        [TestCaseSource(nameof(GetBitlistData))]
        public void Can_deserialize_bitarray_bitlist(bool[] value, ulong limit, string expectedByteString, byte[] expectedHashTreeRoot)
        {
            // Arrange
            var encoded = Bytes.FromHexString(expectedByteString);

            // Act
            BitArray decoded = Ssz.DecodeBitlist(encoded);

            // Assert
            BitArray expected = new BitArray(value);
            decoded.ShouldBe(expected);
        }

        [TestCaseSource(nameof(GetBitlistData))]
        public void Can_merkleize_bitarray_bitlist(bool[] value, ulong maximumBitlistLength, string expectedByteString, byte[] expectedHashTreeRoot)
        {
            // Arrange
            var input = new BitArray(value);

            // Act
            var hashTreeRoot = new byte[32];
            Merkle.IzeBitlist(out UInt256 root, input, maximumBitlistLength);
            root.ToLittleEndian(hashTreeRoot);

            // Assert
            hashTreeRoot.ToArray().ShouldBe(expectedHashTreeRoot);
        }

        public static IEnumerable<object[]> GetBitvectorData()
        {
            yield return new object[] {
                new[] { true, true, false, true, false, true, false, false },
                "2b",
                HashUtility.Chunk(new byte[] { 0x2b }).ToArray()
            };

            yield return new object[] {
                new[] { false, true, false, true },
                "0a",
                HashUtility.Chunk(new byte[] { 0x0a }).ToArray()
            };

            yield return new object[] {
                new[] { false, true, false },
                "02",
                HashUtility.Chunk(new byte[] { 0x02 }).ToArray()
            };

            yield return new object[] {
                new[] { true, false, true, false, false, false, true, true, false, true },
                "c502",
                HashUtility.Chunk(new byte[] { 0xc5, 0x02 }).ToArray()
            };

            yield return new object[] {
                new[] { true, false, true, false, false, false, true, true,
                    false, true, false, false, false, false, true, true },
                "c5c2",
                HashUtility.Chunk(new byte[] { 0xc5, 0xc2 }).ToArray()
            };

            yield return new object[] {
                Enumerable.Repeat(true, 512).ToArray(),
                new string('f', 64 * 2),
                HashUtility.Hash(
                    Enumerable.Repeat((byte)0xff, 32).ToArray(),
                    Enumerable.Repeat((byte)0xff, 32).ToArray()
                )
            };

            yield return new object[] {
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
            };
        }

        public static IEnumerable<object[]> GetBitlistData()
        {
            yield return new object[] {
                new[] { true, true, false, true, false, true, false, false },
                (ulong)8,
                "2b01",
                HashUtility.Hash(
                    HashUtility.Chunk(new byte[] { 0x2b }).ToArray(),
                    HashUtility.Chunk(new byte[] { 0x08 }).ToArray()
                )
            };

            yield return new object[] {
                new[] { false, true, false, true },
                (ulong)4,
                "1a",
                HashUtility.Hash(
                    HashUtility.Chunk(new byte[] { 0x0a }).ToArray(),
                    HashUtility.Chunk(new byte[] { 0x04 }).ToArray()
                )
            };

            yield return new object[] {
                new[] { false, true, false },
                (ulong)3,
                "0a",
                HashUtility.Hash(
                    HashUtility.Chunk(new byte[] { 0x02 }).ToArray(),
                    HashUtility.Chunk(new byte[] { 0x03 }).ToArray()
                )
            };

            yield return new object[] {
                new[] { true, false, true, false, false, false, true, true, false, true },
                (ulong)16,
                "c506",
                HashUtility.Hash(
                    HashUtility.Chunk(new byte[] { 0xc5, 0x02 }).ToArray(),
                    HashUtility.Chunk(new byte[] { 0x0a }).ToArray()
                )
            };

            yield return new object[] {
                new[] { true, false, true, false, false, false, true, true,
                    false, true, false, false, false, false, true, true },
                (ulong)16,
                "c5c201",
                HashUtility.Hash(
                    HashUtility.Chunk(new byte[] { 0xc5, 0xc2 }).ToArray(),
                    HashUtility.Chunk(new byte[] { 0x10 }).ToArray()
                )
            };

            yield return new object[] {
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
            };

            yield return new object[] {
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
            };

            yield return new object[] {
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
            };
        }

    }
}