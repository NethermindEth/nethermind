//using System;
//using Microsoft.VisualStudio.TestTools.UnitTesting;
//using Shouldly;

//namespace Cortex.Cryptography.Tests
//{
//    [TestClass]
//    public class BLSHerumiTest
//    {
//        // 'Use Stack' test generated private key, got public key, signed a message, then verified the signature

//        private const string Hash1 = "0100000000000000000000000000000000000000000000000000000000000000";
//        private const string PrivateKey1 = "0100000000000000000000000000000000000000000000000000000000000000";

//        // These values grabbed from running the tests; need external test vectors to verify
//        private const string PublicKey1 = "A4F58F3D9EE829F9A853F80B0E32C2981BE883A537F0C21AD4AF17BE22E6E9959915EC21B7F9D8CC4C7315F31F3600E5";
//        private const string Signature1 = "B71DF7A5080F908A16C2658EA90164E28C924C3F0E6655F6D82ADCA6BFBDFB5F9EFCA82C1609676FA15CD30396F1A4B30F3D011AF81ACF00140AAB3C122C61BBDF0628DB81C37664BDFC828163CE074EE33A1A5CE5488556603BC5D8D9F21ECC";

//        private const string Signature0 = "B71DF7A5080F908A16C2658EA90164E28C924C3F0E6655F6D82ADCA6BFBDFB5F9EFCA82C1609676FA15CD30396F1A4B30F3D011AF81ACF00140AAB3C122C61BBDF0628DB81C37664BDFC828163CE074EE33A1A5CE5488556603BC5D8D9F21ECC";

//        //private const string Data2 = "01000000000000000000000000000000000000000000000000000000000000000300000000000000";

//        [TestMethod]
//        public void PublicKeyFromPrivateKey()
//        {
//            // Arrange
//            var privateKey = HexMate.Convert.FromHexString(PrivateKey1);
//            var parameters = new BLSParameters()
//            {
//                PrivateKey = privateKey
//            };
//            Console.WriteLine("Private Key: [{0}] {1}", privateKey.Length, HexMate.Convert.ToHexString(privateKey));

//            // Act
//            using var bls = new BLSHerumi(parameters);
//            var result = new byte[48];
//            var success = bls.TryExportBLSPublicKey(result.AsSpan(), out var bytesWritten);

//            Console.WriteLine("Public Key: [{0}] {1}", result.Length, HexMate.Convert.ToHexString(result));

//            // Assert
//            success.ShouldBeTrue();
//            bytesWritten.ShouldBe(48);
//            result.ShouldBe(HexMate.Convert.FromHexString(PublicKey1));
//        }

//        [TestMethod]
//        public void SigningHashShouldWork()
//        {
//            // Arrange
//            var privateKey = HexMate.Convert.FromHexString(PrivateKey1);
//            var parameters = new BLSParameters()
//            {
//                PrivateKey = privateKey
//            };
//            Console.WriteLine("Private Key: [{0}] {1}", privateKey.Length, HexMate.Convert.ToHexString(privateKey));
//            var hash = HexMate.Convert.FromHexString(Hash1);

//            // Act
//            using var bls = new BLSHerumi(parameters);
//            var result = new byte[96];
//            var success = bls.TrySignHash(hash.AsSpan(), result.AsSpan(), out var bytesWritten);

//            Console.WriteLine("Signature: [{0}] {1}", result.Length, HexMate.Convert.ToHexString(result));

//            // Assert
//            success.ShouldBeTrue();
//            bytesWritten.ShouldBe(96);
//            result.ShouldBe(HexMate.Convert.FromHexString(Signature1));
//        }

//        [TestMethod]
//        public void SigningEmptyHashShouldWork()
//        {
//            // Arrange
//            var privateKey = HexMate.Convert.FromHexString(PrivateKey1);
//            var parameters = new BLSParameters()
//            {
//                PrivateKey = privateKey
//            };
//            var hash = new byte[32];
//            // Empty hash does *not* work.
//            hash[0] = 0x01;

//            // Act
//            using var bls = new BLSHerumi(parameters);
//            var result = new byte[96];
//            var success = bls.TrySignHash(hash.AsSpan(), result.AsSpan(), out var bytesWritten);

//            Console.WriteLine("Signature: [{0}] {1}", result.Length, HexMate.Convert.ToHexString(result));

//            // Assert
//            success.ShouldBeTrue();
//            bytesWritten.ShouldBe(96);
//            result.ShouldBe(HexMate.Convert.FromHexString(Signature0));
//        }


//        //[TestMethod]
//        //public void SigningDataShouldWork()
//        //{
//        //    // Arrange
//        //    var privateKey = HexMate.Convert.FromHexString(PrivateKey1);
//        //    var parameters = new BLSParameters()
//        //    {
//        //        PrivateKey = privateKey
//        //    };
//        //    Console.WriteLine("Private Key: [{0}] {1}", privateKey.Length, HexMate.Convert.ToHexString(privateKey));
//        //    var data = HexMate.Convert.FromHexString(Data2);

//        //    // Act
//        //    using var bls = new BLSHerumi(parameters);
//        //    var result = new byte[96];
//        //    var success = bls.TrySignData(data.AsSpan(), result.AsSpan(), out var bytesWritten);

//        //    Console.WriteLine("Signature: [{0}] {1}", result.Length, HexMate.Convert.ToHexString(result));

//        //    // Assert
//        //    success.ShouldBeTrue();
//        //    bytesWritten.ShouldBe(96);
//        //    //result.ShouldBe(HexMate.Convert.FromHexString(Signature2));
//        //}

//        [TestMethod]
//        public void VerifyShouldWork()
//        {
//            // Arrange
//            var publicKey = HexMate.Convert.FromHexString(PublicKey1);
//            var parameters = new BLSParameters()
//            {
//                PublicKey = publicKey
//            };
//            var hash = HexMate.Convert.FromHexString(Hash1);
//            var signature = HexMate.Convert.FromHexString(Signature1);

//            // Act
//            using var bls = new BLSHerumi(parameters);
//            var success = bls.VerifyHash(hash.AsSpan(), signature.AsSpan());

//            // Assert
//            success.ShouldBeTrue();
//        }

//        [TestMethod]
//        public void VerifyModifiedHashShouldFail()
//        {
//            // Arrange
//            var publicKey = HexMate.Convert.FromHexString(PublicKey1);
//            var parameters = new BLSParameters()
//            {
//                PublicKey = publicKey
//            };
//            var hash = HexMate.Convert.FromHexString(Hash1);
//            hash[0] = 0x02;
//            var signature = HexMate.Convert.FromHexString(Signature1);

//            // Act
//            using var bls = new BLSHerumi(parameters);
//            var success = bls.VerifyHash(hash.AsSpan(), signature.AsSpan());

//            // Assert
//            success.ShouldBeFalse();
//        }

//    }
//}
