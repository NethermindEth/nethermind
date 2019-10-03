using System;
using System.Collections.Generic;
using Cortex.SimpleSerialize.Tests.Containers;
using Cortex.SimpleSerialize.Tests.Ssz;
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
            var container = new SmallTestContainer() { A = (ushort)0x4567, B = (ushort)0x0123 };
            var tree = new SszTree(container.ToSszElement());

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
            var container = new SingleFieldTestContainer() { A = (byte)0xab };
            var tree = new SszTree(container.ToSszElement());

            // Act
            var bytes = tree.Serialize();
            var hashTreeRoot = tree.HashTreeRoot();

            // Assert
            var expectedByteString = "ab";
            var byteString = BitConverter.ToString(bytes.ToArray()).Replace("-", "").ToLowerInvariant();
            byteString.ShouldBe(expectedByteString);
            var expectedHashTreeRootString = "ab00000000000000000000000000000000000000000000000000000000000000";
            var hashTreeRootString = BitConverter.ToString(hashTreeRoot.ToArray()).Replace("-", "").ToLowerInvariant();
            hashTreeRootString.ShouldBe(expectedHashTreeRootString);
        }
    }

    namespace Containers
    {
        // Define containers as plain classes

        class SingleFieldTestContainer
        {
            public byte A { get; set; }
        }

        class SmallTestContainer
        {
            public ushort A { get; set; }
            public ushort B { get; set; }
        }
    }

    namespace Ssz
    {
        // Define builder extensions that construct SSZ elements from containers

        static class SingleFieldTestContainerExtensions
        {
            public static SszElement ToSszElement(this SingleFieldTestContainer item)
            {
                return new SszCompositeElement(GetChildren(item));
            }

            private static IEnumerable<SszElement> GetChildren(SingleFieldTestContainer item)
            {
                yield return new SszLeafElement(item.A);
            }
        }

        static class SmallTestContainerExtensions
        {
            public static SszElement ToSszElement(this SmallTestContainer item)
            {
                return new SszCompositeElement(GetChildren(item));
            }

            private static IEnumerable<SszElement> GetChildren(SmallTestContainer item)
            {
                yield return new SszLeafElement(item.A);
                yield return new SszLeafElement(item.B);
            }
        }
    }
}
