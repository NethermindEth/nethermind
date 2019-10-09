using System;
using Cortex.SimpleSerialize.Tests.Containers;
using Cortex.SimpleSerialize.Tests.Ssz;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;
using static Cortex.SimpleSerialize.Tests.HashUtility;

namespace Cortex.SimpleSerialize.Tests
{
    [TestClass]
    public class SszTreeTest_List
    {
        [TestMethod]
        public void SmallTestContainerList16()
        {
            // Arrange
            var value = new SingleFieldTestContainer[] {
                new SingleFieldTestContainer { A = 0x01 },
                new SingleFieldTestContainer { A = 0x02 },
                new SingleFieldTestContainer { A = 0x03 },
            };
            var tree = new SszTree(value.ToSszList(16));

            // Act
            var bytes = tree.Serialize();
            var hashTreeRoot = tree.HashTreeRoot();

            // Assert
            var byteString = BitConverter.ToString(bytes.ToArray()).Replace("-", "").ToLowerInvariant();
            var expectedByteString = "010203";
            byteString.ShouldBe(expectedByteString);

            var contentsRoot = Merge(
                Hash(
                    Hash(
                        Chunk(new byte[] { 0x01 }),
                        Chunk(new byte[] { 0x02 })
                    ),
                    Hash(
                        Chunk(new byte[] { 0x03 }),
                        Chunk(new byte[] { 0x00 })
                    )
                ),
                ZeroHashes(2, 4));
            var expectedHashTreeRoot =
                Hash(
                    contentsRoot,
                    Chunk(new byte[] { 0x03, 0x00, 0x00, 0x00 }) // mix in length
                );
            var expectedHashTreeRootString = BitConverter.ToString(expectedHashTreeRoot).Replace("-", "").ToLowerInvariant();

            var hashTreeRootString = BitConverter.ToString(hashTreeRoot.ToArray()).Replace("-", "").ToLowerInvariant();
            hashTreeRootString.ShouldBe(expectedHashTreeRootString);
        }

    }
}
