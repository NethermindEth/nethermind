using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace Cortex.SimpleSerialize.Tests
{
    [TestClass]
    public class SszNumberTest
    {
        [DataTestMethod]
        [DataRow((byte)0, "00",
    "0000000000000000000000000000000000000000000000000000000000000000")]
        [DataRow((byte)0x01, "01",
    "0100000000000000000000000000000000000000000000000000000000000000")]
        [DataRow((byte)0xab, "ab",
    "ab00000000000000000000000000000000000000000000000000000000000000")]
        public void ByteSerialize(byte value, string expectedByteString, string expectedHashTreeRoot)
        {
            // Arrange
            var node = new SszNumber(value);

            // Act
            var bytes = node.Serialize();
            var hashTreeRoot = node.HashTreeRoot();

            // Assert
            var byteString = BitConverter.ToString(bytes.ToArray()).Replace("-", "").ToLowerInvariant();
            byteString.ShouldBe(expectedByteString);
            var hashTreeRootString = BitConverter.ToString(hashTreeRoot.ToArray()).Replace("-", "").ToLowerInvariant();
            hashTreeRootString.ShouldBe(expectedHashTreeRoot);
        }

        [DataTestMethod]
        [DataRow((ushort)0, "0000",
"0000000000000000000000000000000000000000000000000000000000000000")]
        [DataRow((ushort)0xabcd, "cdab",
"cdab000000000000000000000000000000000000000000000000000000000000")]
        public void ByteUInt16(ushort value, string expectedByteString, string expectedHashTreeRoot)
        {
            // Arrange
            var node = new SszNumber(value);

            // Act
            var bytes = node.Serialize();
            var hashTreeRoot = node.HashTreeRoot();

            // Assert
            var byteString = BitConverter.ToString(bytes.ToArray()).Replace("-", "").ToLowerInvariant();
            byteString.ShouldBe(expectedByteString);
            var hashTreeRootString = BitConverter.ToString(hashTreeRoot.ToArray()).Replace("-", "").ToLowerInvariant();
            hashTreeRootString.ShouldBe(expectedHashTreeRoot);
        }

        [DataTestMethod]
        [DataRow((uint)0, "00000000",
"0000000000000000000000000000000000000000000000000000000000000000")]
        [DataRow((uint)0x01234567, "67452301",
"6745230100000000000000000000000000000000000000000000000000000000")]
        public void ByteUInt32(uint value, string expectedByteString, string expectedHashTreeRoot)
        {
            // Arrange
            var node = new SszNumber(value);

            // Act
            var bytes = node.Serialize();
            var hashTreeRoot = node.HashTreeRoot();

            // Assert
            var byteString = BitConverter.ToString(bytes.ToArray()).Replace("-", "").ToLowerInvariant();
            byteString.ShouldBe(expectedByteString);
            var hashTreeRootString = BitConverter.ToString(hashTreeRoot.ToArray()).Replace("-", "").ToLowerInvariant();
            hashTreeRootString.ShouldBe(expectedHashTreeRoot);
        }

        [DataTestMethod]
        [DataRow((ulong)0, "0000000000000000", 
            "0000000000000000000000000000000000000000000000000000000000000000")]
        [DataRow((ulong)0x0123456789abcdef, "efcdab8967452301",
            "efcdab8967452301000000000000000000000000000000000000000000000000")]
        public void UInt64Serialize(ulong value, string expectedByteString, string expectedHashTreeRoot)
        {
            // Arrange
            var node = new SszNumber(value);

            // Act
            var bytes = node.Serialize();
            var hashTreeRoot = node.HashTreeRoot();

            // Assert
            var byteString = BitConverter.ToString(bytes.ToArray()).Replace("-", "").ToLowerInvariant();
            byteString.ShouldBe(expectedByteString);
            var hashTreeRootString = BitConverter.ToString(hashTreeRoot.ToArray()).Replace("-", "").ToLowerInvariant();
            hashTreeRootString.ShouldBe(expectedHashTreeRoot);
        }
    }
}
