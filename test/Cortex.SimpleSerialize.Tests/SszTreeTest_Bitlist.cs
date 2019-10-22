using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace Cortex.SimpleSerialize.Tests
{
    [TestClass]
    public class SszTreeTest_Bitlist
    {
        [DataTestMethod]
        [DynamicData(nameof(GetData), DynamicDataSourceType.Method)]
        public void BitlistSerialize(bool[] value, ulong limit, string expectedByteString, byte[] expectedHashTreeRoot)
        {
            // Arrange
            var tree = new SszTree(new SszBitlist(value, limit));

            // Act
            var bytes = tree.Serialize();
            var hashTreeRoot = tree.HashTreeRoot();

            // Assert
            var byteString = BitConverter.ToString(bytes.ToArray()).Replace("-", "").ToLowerInvariant();
            byteString.ShouldBe(expectedByteString);
            hashTreeRoot.ToArray().ShouldBe(expectedHashTreeRoot);
        }

        public static IEnumerable<object[]> GetData()
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
