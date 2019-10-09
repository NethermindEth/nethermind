using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;
using static Cortex.SimpleSerialize.Tests.HashUtility;

namespace Cortex.SimpleSerialize.Tests
{
    [TestClass]
    public class SszTreeTest_BasicList
    {
        [TestMethod]
        public void UInt16List32()
        {
            // Arrange
            var value = new ushort[] { 0xaabb, 0xc0ad, 0xeeff };
            var tree = new SszTree(new SszBasicList(value, limit: 32));

            // Act
            var bytes = tree.Serialize();
            var hashTreeRoot = tree.HashTreeRoot();

            // Assert
            var byteString = BitConverter.ToString(bytes.ToArray()).Replace("-", "").ToLowerInvariant();
            var expectedByteString = "bbaaadc0ffee";
            byteString.ShouldBe(expectedByteString);

            var contentsRoot = Hash(
                        Chunk(new byte[] { 0xbb, 0xaa, 0xad, 0xc0, 0xff, 0xee }),
                        Chunk(new byte[0])
                    );

            var expectedHashTreeRoot =
                Hash(
                    contentsRoot,
                    Chunk(new byte[] { 0x03, 0x00, 0x00, 0x00 })
                );
            var expectedHashTreeRootString = BitConverter.ToString(expectedHashTreeRoot).Replace("-", "").ToLowerInvariant();

            var hashTreeRootString = BitConverter.ToString(hashTreeRoot.ToArray()).Replace("-", "").ToLowerInvariant();
            hashTreeRootString.ShouldBe(expectedHashTreeRootString);
        }

        [TestMethod]
        public void UInt32List128()
        {
            // Arrange
            var value = new uint[] { 0xaabb, 0xc0ad, 0xeeff };
            var tree = new SszTree(new SszBasicList(value, limit: 128));

            // Act
            var bytes = tree.Serialize();
            var hashTreeRoot = tree.HashTreeRoot();

            // Assert
            var byteString = BitConverter.ToString(bytes.ToArray()).Replace("-", "").ToLowerInvariant();
            var expectedByteString = "bbaa0000adc00000ffee0000";
            byteString.ShouldBe(expectedByteString);

            var expectedHashTreeRoot =
                Hash(
                    Merge(
                        Chunk(new byte[] { 0xbb, 0xaa, 0x00, 0x00, 
                            0xad, 0xc0, 0x00, 0x00, 
                            0xff, 0xee, 0x00, 0x00 }),
                        ZeroHashes(0, 4)
                    ),
                    Chunk(new byte[] { 0x03, 0x00, 0x00, 0x00 })
                );

            var expectedHashTreeRootString = BitConverter.ToString(expectedHashTreeRoot).Replace("-", "").ToLowerInvariant();

            var hashTreeRootString = BitConverter.ToString(hashTreeRoot.ToArray()).Replace("-", "").ToLowerInvariant();
            // TODO: Need to mix in the length
            hashTreeRootString.ShouldBe(expectedHashTreeRootString);
        }

    }
}
