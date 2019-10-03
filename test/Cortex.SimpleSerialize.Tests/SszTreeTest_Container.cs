using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;
using static Cortex.SimpleSerialize.Tests.HashUtility;

namespace Cortex.SimpleSerialize.Tests
{
    [TestClass]
    public class SszTreeTest_Container
    {
        [TestMethod]
        public void SmallContainer()
        {
            // Arrange
            var tree = new SszTree(
                new SszCompositeElement(new [] {
                    new SszLeafElement((ushort)0x4567),
                    new SszLeafElement((ushort)0x0123)
                })
            );

            // Act
            var bytes = tree.Serialize();
            var hashTreeRoot = tree.HashTreeRoot();

            // Assert
            var expectedByteString = "67452301";
            var byteString = BitConverter.ToString(bytes.ToArray()).Replace("-", "").ToLowerInvariant();
            byteString.ShouldBe(expectedByteString);

            var c1 = new byte[32];
            c1[0] = 0x67;
            c1[1] = 0x45;
            var c2 = new byte[32];
            c2[0] = 0x23;
            c2[1] = 0x01;
            var expectedHashTreeRoot = Hash(c1, c2);
            var expectedHashTreeRootString = BitConverter.ToString(expectedHashTreeRoot).Replace("-", "").ToLowerInvariant();
            var hashTreeRootString = BitConverter.ToString(hashTreeRoot.ToArray()).Replace("-", "").ToLowerInvariant();
            hashTreeRootString.ShouldBe(expectedHashTreeRootString);
        }

        [TestMethod]
        public void SingleFieldContainer()
        {
            // Arrange
            var node = new SszContainer(new SszNode[] {
                new SszNumber((byte)0xab)
            });
            var tree = new SszTree(
                new SszCompositeElement(new[] {
                    new SszLeafElement((byte)0xab)
                })
            );

            // Act
            var bytes = node.Serialize();
            var hashTreeRoot = node.HashTreeRoot();

            // Assert
            var expectedByteString = "ab";
            var byteString = BitConverter.ToString(bytes.ToArray()).Replace("-", "").ToLowerInvariant();
            byteString.ShouldBe(expectedByteString);
            var expectedHashTreeRootString = "ab00000000000000000000000000000000000000000000000000000000000000";
            var hashTreeRootString = BitConverter.ToString(hashTreeRoot.ToArray()).Replace("-", "").ToLowerInvariant();
            hashTreeRootString.ShouldBe(expectedHashTreeRootString);
        }
    }
}
