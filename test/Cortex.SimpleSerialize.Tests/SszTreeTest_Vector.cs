using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;
using static Cortex.SimpleSerialize.Tests.HashUtility;

namespace Cortex.SimpleSerialize.Tests
{
    [TestClass]
    public class SszTreeTest_Vector
    {
        [TestMethod]
        public void UInt16Vector()
        {
            // Arrange
            var value = new ushort[] { 0x4567, 0x0123 };
            var tree = new SszTree(new SszBasicVector(value));

            // Act
            var bytes = tree.Serialize();
            var hashTreeRoot = tree.HashTreeRoot();

            // Assert
            var byteString = BitConverter.ToString(bytes.ToArray()).Replace("-", "").ToLowerInvariant();
            var expectedByteString = "67452301";
            byteString.ShouldBe(expectedByteString);

            var expectedHashTreeRoot = Chunk(new byte[] { 0x67, 0x45, 0x23, 0x01 }).ToArray();
            var expectedHashTreeRootString = BitConverter.ToString(expectedHashTreeRoot).Replace("-", "").ToLowerInvariant();

            var hashTreeRootString = BitConverter.ToString(hashTreeRoot.ToArray()).Replace("-", "").ToLowerInvariant();
            hashTreeRootString.ShouldBe(expectedHashTreeRootString);
        }

    }
}
