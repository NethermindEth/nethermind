using System;
using Shouldly;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cortex.Cryptography.Tests
{
    [TestClass]
    public class BLSHerumiTest
    {
        // 'Use Stack' test generated private key, got public key, signed a message, then verified the signature

        [TestMethod]
        public void PublicKeyFromPrivateKey()
        {
            // Arrange
            var privateKey = new byte[32];
            privateKey[0] = (byte)0x01;
            var parameters = new BLSParameters()
            {
                PrivateKey = privateKey
            };
            Console.WriteLine("Private Key: [{0}] {1}", privateKey.Length, BitConverter.ToString(privateKey));

            // Act
            using var bls = new BLSHerumi(parameters);
            var result = new byte[48];
            var success = bls.TryExportBLSPublicKey(result.AsSpan(), out var bytesWritten);

            Console.WriteLine("Public Key: [{0}] {1}", result.Length, BitConverter.ToString(result));

            // Assert
            success.ShouldBeTrue();
            bytesWritten.ShouldBe(48);
        }


        [TestMethod]
        public void SigningShouldWork()
        {
            // Arrange
            var privateKey = new byte[32];
            privateKey[0] = 0x01;
            var parameters = new BLSParameters()
            {
                PrivateKey = privateKey
            };
            Console.WriteLine("Private Key: [{0}] {1}", privateKey.Length, BitConverter.ToString(privateKey));
            var hash = new byte[32];
            hash[0] = 0x01;

            // Act
            using var bls = new BLSHerumi(parameters);
            var result = new byte[96];
            var success = bls.TrySignHash(hash.AsSpan(), result.AsSpan(), out var bytesWritten);

            Console.WriteLine("Signature: [{0}] {1}", result.Length, BitConverter.ToString(result));

            // Assert
            success.ShouldBeTrue();
            bytesWritten.ShouldBe(96);
        }

    }
}
