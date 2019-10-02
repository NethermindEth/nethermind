using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;
namespace Cortex.SimpleSerialize.Tests
{
    [TestClass]
    public class SszByteArrayTest
    {
        private readonly HashAlgorithm hash = SHA256.Create();

        [DataTestMethod]
        public void ByteArray16Serialize()
        {
            // Arrange
            var value = new byte[16];
            value[0] = 1;
            value[15] = 0xff;
            var node = new SszByteArray(value);

            // Act
            var bytes = node.Serialize();
            var hashTreeRoot = node.HashTreeRoot();

            // Assert
            var byteString = BitConverter.ToString(bytes.ToArray()).Replace("-", "").ToLowerInvariant();
            var expectedByteString = "010000000000000000000000000000ff";
            byteString.ShouldBe(expectedByteString);

            var hashTreeRootString = BitConverter.ToString(hashTreeRoot.ToArray()).Replace("-", "").ToLowerInvariant();
            var expectedHashTreeRootString = "010000000000000000000000000000ff"
                                           + "00000000000000000000000000000000";
            hashTreeRootString.ShouldBe(expectedHashTreeRootString);
        }

        [DataTestMethod]
        public void ByteArray32Serialize()
        {
            // Arrange
            var value = new byte[32];
            value[0] = 1;
            value[31] = 0xff;
            var node = new SszByteArray(value);

            // Act
            var bytes = node.Serialize();
            var hashTreeRoot = node.HashTreeRoot();

            // Assert
            var byteString = BitConverter.ToString(bytes.ToArray()).Replace("-", "").ToLowerInvariant();
            var expectedByteString = "01000000000000000000000000000000000000000000000000000000000000ff";
            byteString.ShouldBe(expectedByteString);

            var hashTreeRootString = BitConverter.ToString(hashTreeRoot.ToArray()).Replace("-", "").ToLowerInvariant();
            var expectedHashTreeRootString = "01000000000000000000000000000000000000000000000000000000000000ff";
            hashTreeRootString.ShouldBe(expectedHashTreeRootString);
        }

        [DataTestMethod]
        public void ByteArray64Serialize()
        {
            // Arrange
            var value = new byte[64];
            value[0] = 1;
            value[32] = 2;
            var node = new SszByteArray(value);

            // Act
            var bytes = node.Serialize();
            var hashTreeRoot = node.HashTreeRoot();

            // Assert
            var byteString = BitConverter.ToString(bytes.ToArray()).Replace("-", "").ToLowerInvariant();
            var expectedByteString = "0100000000000000000000000000000000000000000000000000000000000000"
                + "0200000000000000000000000000000000000000000000000000000000000000";
            byteString.ShouldBe(expectedByteString);

            var hashTreeRootString = BitConverter.ToString(hashTreeRoot.ToArray()).Replace("-", "").ToLowerInvariant();
            Console.WriteLine(hashTreeRootString);
            //var c1 = new byte[32];
            //c1[0] = 1;
            //var c2 = new byte[32];
            //c2[0] = 2;
            //var expectedHashTreeRoot = Hash(c1, c2);
            var expectedHashTreeRootString = "ff55c97976a840b4ced964ed49e3794594ba3f675238b5fd25d282b60f70a194";
            hashTreeRootString.ShouldBe(expectedHashTreeRootString);
        }

        [DataTestMethod]
        public void ByteArray96Serialize()
        {
            // Arrange
            var value = new byte[96];
            value[0] = 1;
            value[32] = 2;
            value[64] = 3;
            value[95] = 0xff;
            var node = new SszByteArray(value);

            // Act
            var bytes = node.Serialize();
            var hashTreeRoot = node.HashTreeRoot();

            // Assert
            var expectedByteString = "0100000000000000000000000000000000000000000000000000000000000000"
                + "0200000000000000000000000000000000000000000000000000000000000000"
                + "03000000000000000000000000000000000000000000000000000000000000ff";
            var byteString = BitConverter.ToString(bytes.ToArray()).Replace("-", "").ToLowerInvariant();
            byteString.ShouldBe(expectedByteString);

            var c1 = new byte[32];
            c1[0] = 1;
            var c2 = new byte[32];
            c2[0] = 2;
            var c3 = new byte[32];
            c3[0] = 3;
            c3[31] = 0xff;
            var cZ = new byte[32];
            var h1 = Hash(c1, c2);
            var expectedHashTreeRoot = Hash(h1, Hash(c3, cZ));
            hashTreeRoot.ToArray().ShouldBe(expectedHashTreeRoot);
        }

        [DataTestMethod]
        public void ByteArray192Serialize()
        {
            // Arrange
            var value = new byte[192];
            value[0] = 1;
            value[32] = 2;
            value[64] = 3;
            value[95] = 0xff;
            value[96] = 4;
            value[128] = 5;
            value[160] = 6;
            var node = new SszByteArray(value);

            // Act
            var bytes = node.Serialize();
            var hashTreeRoot = node.HashTreeRoot();

            // Assert
            var expectedByteString = "0100000000000000000000000000000000000000000000000000000000000000"
                + "0200000000000000000000000000000000000000000000000000000000000000"
                + "03000000000000000000000000000000000000000000000000000000000000ff"
                + "0400000000000000000000000000000000000000000000000000000000000000"
                + "0500000000000000000000000000000000000000000000000000000000000000"
                + "0600000000000000000000000000000000000000000000000000000000000000";
            var byteString = BitConverter.ToString(bytes.ToArray()).Replace("-", "").ToLowerInvariant();
            byteString.ShouldBe(expectedByteString);

            var c1 = new byte[32];
            c1[0] = 1;
            var c2 = new byte[32];
            c2[0] = 2;
            var c3 = new byte[32];
            c3[0] = 3;
            c3[31] = 0xff;
            var c4 = new byte[32];
            c4[0] = 4;
            var c5 = new byte[32];
            c5[0] = 5;
            var c6 = new byte[32];
            c6[0] = 6;
            var cZ = new byte[32];

            var expectedHashTreeRoot = Hash(
                Hash(Hash(c1, c2), Hash(c3, c4)), 
                Hash(Hash(c5, c6), Hash(cZ, cZ)));
            hashTreeRoot.ToArray().ShouldBe(expectedHashTreeRoot);
        }

        private byte[] Hash(byte[] c1, byte[] c2)
        {
            var b = new byte[64];
            c1.CopyTo(b, 0);
            c2.CopyTo(b, 32);
            return hash.ComputeHash(b);
        }
    }
}
