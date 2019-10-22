using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace Cortex.SimpleSerialize.Tests
{
    [TestClass]
    public class SszTreeTest_Bitvector
    {
        [DataTestMethod]
        [DynamicData(nameof(GetData), DynamicDataSourceType.Method)]
        public void BitvectorSerialize(bool[] value, string expectedByteString, byte[] expectedHashTreeRoot)
        {
            // Arrange
            var tree = new SszTree(new SszBitvector(value));

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
    }
}
