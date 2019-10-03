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

            var expectedHashTreeRoot = Hash(Chunk(new byte[] { 0x67, 0x45 }), Chunk(new byte[] { 0x23, 0x01 }));
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

        [TestMethod]
        public void FixedContainer()
        {
            // Arrange
            var container = new FixedTestContainer()
            {
                A = (byte)0xab,
                B = (ulong)0xaabbccdd00112233,
                C = (uint)0x12345678
            };
            var tree = new SszTree(container.ToSszElement());

            // Act
            var bytes = tree.Serialize();
            var hashTreeRoot = tree.HashTreeRoot();

            // Assert
            var expectedByteString = "ab33221100ddccbbaa78563412";
            var byteString = BitConverter.ToString(bytes.ToArray()).Replace("-", "").ToLowerInvariant();
            byteString.ShouldBe(expectedByteString);

            var expectedHashTreeRoot = Hash(
                Hash(Chunk(new byte[] { 0xab }), Chunk(new byte[] { 0x33, 0x22, 0x11, 0x00, 0xdd, 0xcc, 0xbb, 0xaa })),
                Hash(Chunk(new byte[] { 0x78, 0x56, 0x34, 0x12 }), Chunk(new byte[] { }))
            );
            var expectedHashTreeRootString = BitConverter.ToString(expectedHashTreeRoot).Replace("-", "").ToLowerInvariant();

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

        class FixedTestContainer
        {
            public byte A { get; set; }
            public ulong B { get; set; }
            public uint C { get; set; }
        }

        class VarTestContainer
        {
            public ushort A { get; set; }
            public IList<ushort> B { get; set; } // max = 1024
            public byte C { get; set; }
        }

        //class ComplexTestContainer
        //{
        //    public ushort A { get; set; }
        //    public IList<ushort> B { get; set; } // max = 128
        //    public byte C { get; set; }
        //    public byte[] D { get; set; } // length = 256
        //    public VarTestContainer E { get; set; }
        //    public FixedTestContainer[] F { get; set; }// length = 4
        //    public VarTestContainer[] G { get; set; } // length = 2
        //}
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

        static class FixedTestContainerExtensions
        {
            public static SszElement ToSszElement(this FixedTestContainer item)
            {
                return new SszCompositeElement(GetChildren(item));
            }

            private static IEnumerable<SszElement> GetChildren(FixedTestContainer item)
            {
                yield return new SszLeafElement(item.A);
                yield return new SszLeafElement(item.B);
                yield return new SszLeafElement(item.C);
            }
        }

        static class VarTestContainerExtensions
        {
            public static SszElement ToSszElement(this VarTestContainer item)
            {
                return new SszCompositeElement(GetChildren(item));
            }

            private static IEnumerable<SszElement> GetChildren(VarTestContainer item)
            {
                yield return new SszLeafElement(item.A);
                //yield return new SszLeafElement(item.B);
                yield return new SszLeafElement(item.C);
            }
        }
    }
}
