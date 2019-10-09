using System;
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
        public void FixedContainer()
        {
            // Arrange
            var container = new FixedTestContainer()
            {
                A = (byte)0xab,
                B = (ulong)0xaabbccdd00112233,
                C = (uint)0x12345678
            };
            var tree = new SszTree(container.ToSszContainer());

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

        [TestMethod]
        public void SingleFieldContainer()
        {
            // Arrange
            var container = new SingleFieldTestContainer() { A = (byte)0xab };
            var tree = new SszTree(container.ToSszContainer());

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
        public void SmallContainer()
        {
            // Arrange
            var container = new SmallTestContainer() { A = (ushort)0x4567, B = (ushort)0x0123 };
            var tree = new SszTree(container.ToSszContainer());

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
        public void VarContainer_Some()
        {
            // Arrange
            var container = new VarTestContainer()
            {
                A = 0xabcd,
                B = new ushort[] { 1, 2, 3 },
                C = 0xff
            };
            var tree = new SszTree(container.ToSszContainer());

            // Act
            var bytes = tree.Serialize();
            var hashTreeRoot = tree.HashTreeRoot();

            // Assert
            var expectedByteString = "cdab07000000ff010002000300";
            var byteString = BitConverter.ToString(bytes.ToArray()).Replace("-", "").ToLowerInvariant();
            byteString.ShouldBe(expectedByteString);

            var expectedHashTreeRoot =
                Hash(
                    Hash(
                        Chunk(new byte[] { 0xcd, 0xab }),
                        Hash(
                            Merge(
                                Chunk(new byte[] { 0x01, 0x00, 0x02, 0x00, 0x03, 0x00 }),
                                ZeroHashes(0, 6)
                            ),
                            Chunk(new byte[] { 0x03, 0x00, 0x00, 0x00 }) // Length mix in
                        )
                    ),
                    Hash(Chunk(new byte[] { 0xff }), Chunk(new byte[] { }))
                );
            var expectedHashTreeRootString = BitConverter.ToString(expectedHashTreeRoot).Replace("-", "").ToLowerInvariant();

            var hashTreeRootString = BitConverter.ToString(hashTreeRoot.ToArray()).Replace("-", "").ToLowerInvariant();
            hashTreeRootString.ShouldBe(expectedHashTreeRootString);
        }
    }
}
