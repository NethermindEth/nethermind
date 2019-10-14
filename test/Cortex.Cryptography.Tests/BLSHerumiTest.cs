using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace Cortex.Cryptography.Tests
{
    [TestClass]
    public class BLSHerumiTest
    {
        // 'Use Stack' test generated private key, got public key, signed a message, then verified the signature

        private const string Hash1 = "0100000000000000000000000000000000000000000000000000000000000000";
        private const string PrivateKey1 = "0100000000000000000000000000000000000000000000000000000000000000";

        // These values grabbed from running the tests; need external test vectors to verify
        private const string PublicKey1 = "957A4A234395850548A1BA495430D76007AA56704C57B1FB51C12519C918C205870779B4D737FE44095CEA370145BF0F";
        private const string Signature1 = "DA859B60498431D02447ADFEA644FCF58570B20292FAFEA0FCFDDF097DABB6247A76A63DE3C92DBE14E6C8A22CCE330682C77879DF954166BE226F801D596D7A99C11920E66E24D5B63E35E3F495FD227561E6591C5FE9EF29F0619374A55E17";

        [TestMethod]
        public void PublicKeyFromPrivateKey()
        {
            // Arrange
            var privateKey = HexMate.Convert.FromHexString(PrivateKey1);
            var parameters = new BLSParameters()
            {
                PrivateKey = privateKey
            };
            Console.WriteLine("Private Key: [{0}] {1}", privateKey.Length, HexMate.Convert.ToHexString(privateKey));

            // Act
            using var bls = new BLSHerumi(parameters);
            var result = new byte[48];
            var success = bls.TryExportBLSPublicKey(result.AsSpan(), out var bytesWritten);

            Console.WriteLine("Public Key: [{0}] {1}", result.Length, HexMate.Convert.ToHexString(result));

            // Assert
            success.ShouldBeTrue();
            bytesWritten.ShouldBe(48);
            result.ShouldBe(HexMate.Convert.FromHexString(PublicKey1));
        }

        [TestMethod]
        public void SigningShouldWork()
        {
            // Arrange
            var privateKey = HexMate.Convert.FromHexString(PrivateKey1);
            var parameters = new BLSParameters()
            {
                PrivateKey = privateKey
            };
            Console.WriteLine("Private Key: [{0}] {1}", privateKey.Length, HexMate.Convert.ToHexString(privateKey));
            var hash = HexMate.Convert.FromHexString(Hash1);

            // Act
            using var bls = new BLSHerumi(parameters);
            var result = new byte[96];
            var success = bls.TrySignHash(hash.AsSpan(), result.AsSpan(), out var bytesWritten);

            Console.WriteLine("Signature: [{0}] {1}", result.Length, HexMate.Convert.ToHexString(result));

            // Assert
            success.ShouldBeTrue();
            bytesWritten.ShouldBe(96);
            result.ShouldBe(HexMate.Convert.FromHexString(Signature1));
        }

        [TestMethod]
        public void VerifyShouldWork()
        {
            // Arrange
            var publicKey = HexMate.Convert.FromHexString(PublicKey1);
            var parameters = new BLSParameters()
            {
                PublicKey = publicKey
            };
            var hash = HexMate.Convert.FromHexString(Hash1);
            var signature = HexMate.Convert.FromHexString(Signature1);

            // Act
            using var bls = new BLSHerumi(parameters);
            var success = bls.VerifyHash(hash.AsSpan(), signature.AsSpan());

            // Assert
            success.ShouldBeTrue();
        }

        [TestMethod]
        public void VerifyModifiedHashShouldFail()
        {
            // Arrange
            var publicKey = HexMate.Convert.FromHexString(PublicKey1);
            var parameters = new BLSParameters()
            {
                PublicKey = publicKey
            };
            var hash = HexMate.Convert.FromHexString(Hash1);
            hash[0] = 0x02;
            var signature = HexMate.Convert.FromHexString(Signature1);

            // Act
            using var bls = new BLSHerumi(parameters);
            var success = bls.VerifyHash(hash.AsSpan(), signature.AsSpan());

            // Assert
            success.ShouldBeFalse();
        }

    }
}
