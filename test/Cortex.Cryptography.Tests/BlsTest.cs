using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace Cortex.Cryptography.Tests
{
    [TestClass]
    public class BlsTest
    {
        // Check against other test suites, e.g. Artemis
        //input: {privkey: '0x47b8192d77bf871b62e87859d653922725724a5c031afeabc60bcef5ff665138',
        //message: '0x0000000000000000000000000000000000000000000000000000000000000000', domain: '0x0000000000000000'}
        //  output: '0xb9d1bf921b3dd048bdce38c2ceac2a2a8093c864881f2415f22b198de935ffa791707855c1656dc21a7af2d502bb46590151d645f062634c3b2cb79c4ed1c4a4b8b3f19f0f5c76965c651553e83d153ff95353735156eff77692f7a62ae653fb'

        // Older results (seem to be different):
        // https://gist.github.com/ChihChengLiang/328bc1db5d2a47950e5364c11f23052a

        private static IList<byte[]> Domains => new List<byte[]>
        {
            new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
            new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01 },
            new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
            new byte[] { 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
            new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef },
            new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff },
        };

        private static IList<byte[]> MessageHashes => new List<byte[]>
        {
            Enumerable.Repeat((byte)0x00, 32).ToArray(),
            Enumerable.Repeat((byte)0x56, 32).ToArray(),
            Enumerable.Repeat((byte)0xab, 32).ToArray(),
        };

        private static IList<string> PrivateKeys => new List<string>
        {
            "263dbd792f5b1be47ed85f8938c0f29586af0d3ac7b977f21c278fe1462040e3",
            "47b8192d77bf871b62e87859d653922725724a5c031afeabc60bcef5ff665138",
            "328388aff0d4a5b7dc9205abd374e7e98f3cd9f3418edb4eafda5fb16473d216",
        };

        //[TestMethod]
        //public void BlsInitialXAndSign()
        //{
        //    var privateKey = HexMate.Convert.FromHexString(PrivateKeys[0]);
        //    var messageHash = MessageHashes[0];
        //    //var domain = Domains[0];
        //    var domain = new byte[] { 0x00 };

        //    Console.WriteLine("Input:");
        //    Console.WriteLine("Private Key: [{0}] {1}", privateKey.Length, HexMate.Convert.ToHexString(privateKey));
        //    Console.WriteLine("Domain: [{0}] {1}", domain.Length, HexMate.Convert.ToHexString(domain));
        //    Console.WriteLine("MessageHash: [{0}] {1}", messageHash.Length, HexMate.Convert.ToHexString(messageHash));

        //    var parameters = new BLSParameters()
        //    {
        //        PrivateKey = privateKey
        //    };
        //    using var bls = new BLSHerumi(parameters);
        //    var initialX = new byte[96];
        //    _ = bls.TryCombineHashAndDomain(messageHash, domain, initialX, out var _);

        //    Console.WriteLine("InitialX: [{0}] {1}", initialX.Length, HexMate.Convert.ToHexString(initialX));

        //    var result = new byte[96];
        //    var success = bls.TrySignHash(initialX, result.AsSpan(), out var bytesWritten);

        //    Console.WriteLine("Output:");
        //    Console.WriteLine("Signature: {0} [{1}] {2}", success, bytesWritten, HexMate.Convert.ToHexString(result));
        //}

        [TestMethod]
        public void BlsInitialXAndSign()
        {
            // Arrange
            var privateKey = HexMate.Convert.FromHexString(PrivateKeys[1]);
            var messageHash = MessageHashes[0];
            var domain = Domains[0];

            Console.WriteLine("Input:");
            Console.WriteLine("Private Key: [{0}] {1}", privateKey.Length, HexMate.Convert.ToHexString(privateKey));
            Console.WriteLine("Domain: [{0}] {1}", domain.Length, HexMate.Convert.ToHexString(domain));
            Console.WriteLine("MessageHash: [{0}] {1}", messageHash.Length, HexMate.Convert.ToHexString(messageHash));

            // Act
            var parameters = new BLSParameters()
            {
                PrivateKey = privateKey
            };
            using var bls = new BLSHerumi(parameters);

            var publicKey = new byte[48];
            _ = bls.TryExportBLSPublicKey(publicKey, out var _);

            Console.WriteLine("Public Key: [{0}] {1}", publicKey.Length, HexMate.Convert.ToHexString(publicKey));

            var initialX = new byte[96];
            _ = bls.TryCombineHashAndDomain(messageHash, domain, initialX, out var _);

            Console.WriteLine("InitialX: [{0}] {1}", initialX.Length, HexMate.Convert.ToHexString(initialX));

            var signature = new byte[96];
            var signatureSuccess = bls.TrySignHash(initialX, signature.AsSpan(), out var bytesWritten);

            Console.WriteLine("Signature: {0} [{1}] {2}", signatureSuccess, bytesWritten, HexMate.Convert.ToHexString(signature));

            //var expectedSignature = HexMate.Convert.FromHexString("b9d1bf921b3dd048bdce38c2ceac2a2a8093c864881f2415f22b198de935ffa791707855c1656dc21a7af2d502bb46590151d645f062634c3b2cb79c4ed1c4a4b8b3f19f0f5c76965c651553e83d153ff95353735156eff77692f7a62ae653fb");
            //signature.ShouldBe(expectedSignature);

            var verifySuccess = bls.VerifyHash(initialX, signature);
            Console.WriteLine("Verify1: {0}", verifySuccess);

            var parameters2 = new BLSParameters()
            {
                PublicKey = publicKey
            };
            using var bls2 = new BLSHerumi(parameters);

            var verifySuccess2 = bls2.VerifyHash(initialX, signature);
            Console.WriteLine("Verify2: {0}", verifySuccess2);

            verifySuccess2.ShouldBeTrue();
        }

        //private byte[] HashToG2Compressed(byte[] messageHash, byte[] domain)
        //{
        //    var HashAlgorithm = System.Security.Cryptography.SHA256.Create();

        //    var xRealInput = new Span<byte>(new byte[messageHash.Length + domain.Length + 1]);
        //    messageHash.CopyTo(xRealInput);
        //    domain.CopyTo(xRealInput.Slice(messageHash.Length));
        //    xRealInput[messageHash.Length + domain.Length] = 0x01;
        //    var xReal = new Span<byte>(new byte[32]);
        //    var xRealSuccess = HashAlgorithm.TryComputeHash(xRealInput, xReal, out var xRealBytesWritten);
        //    if (!xRealSuccess || xRealBytesWritten != 32)
        //    {
        //        throw new Exception("Error in getting G2 real component from hash.");
        //    }

        //    var xImaginaryInput = new Span<byte>(new byte[messageHash.Length + domain.Length + 1]);
        //    messageHash.CopyTo(xImaginaryInput);
        //    domain.CopyTo(xImaginaryInput.Slice(messageHash.Length));
        //    xImaginaryInput[messageHash.Length + domain.Length] = 0x02;
        //    var xImaginary = new Span<byte>(new byte[32]);
        //    var xImaginarySuccess = HashAlgorithm.TryComputeHash(xImaginaryInput, xImaginary, out var xImaginaryBytesWritten);
        //    if (!xImaginarySuccess || xImaginaryBytesWritten != 32)
        //    {
        //        throw new Exception("Error in getting G2 imaginary component from hash.");
        //    }

        //    Console.WriteLine("xReal Hash: [{0}] {1}", xReal.Length, HexMate.Convert.ToHexString(xReal));
        //    Console.WriteLine("xImaginary Hash: [{0}] {1}", xImaginary.Length, HexMate.Convert.ToHexString(xImaginary));

        //    if (BitConverter.IsLittleEndian)
        //    {
        //        for (var i = 0; i < xReal.Length; i += 8)
        //        {
        //            xReal.Slice(i, 8).Reverse();
        //            xImaginary.Slice(i, 8).Reverse();
        //        }
        //    }

        //    var xFp2 = new Bls384Interop.MclBnFp2();
        //    xFp2.d_0.d_2 = BitConverter.ToUInt64(xReal.Slice(0, 8));
        //    xFp2.d_0.d_3 = BitConverter.ToUInt64(xReal.Slice(8, 8));
        //    xFp2.d_0.d_4 = BitConverter.ToUInt64(xReal.Slice(16, 8));
        //    xFp2.d_0.d_5 = BitConverter.ToUInt64(xReal.Slice(24, 8));
        //    xFp2.d_1.d_2 = BitConverter.ToUInt64(xImaginary.Slice(0, 8));
        //    xFp2.d_1.d_3 = BitConverter.ToUInt64(xImaginary.Slice(8, 8));
        //    xFp2.d_1.d_4 = BitConverter.ToUInt64(xImaginary.Slice(16, 8));
        //    xFp2.d_1.d_5 = BitConverter.ToUInt64(xImaginary.Slice(24, 8));

        //    //var xFp2RealSpan = MemoryMarshal.CreateSpan(ref xFp2.d_0, 1);
        //    //MemoryMarshal.Cast<byte, Bls384Interop.MclBnFp>(xReal).CopyTo(xFp2RealSpan);
        //    //var xFp2ImaginarySpan = MemoryMarshal.CreateSpan(ref xFp2.d_1, 1);
        //    //MemoryMarshal.Cast<byte, Bls384Interop.MclBnFp>(xImaginary).CopyTo(xFp2ImaginarySpan);

        //    var fp2Buffer = new byte[1000];
        //    var fp2BytesWritten = Bls384Interop.mclBnFp2_serialize(fp2Buffer, fp2Buffer.Length, xFp2);
        //    if (fp2BytesWritten != 96)
        //    {
        //        //throw new Exception("Error serializing FP2.");
        //    }

        //    return new Span<byte>(fp2Buffer).Slice(0, fp2BytesWritten).ToArray();
        //}

        [DataTestMethod]
        [DynamicData(nameof(Case03PrivateToPublicKeyData), DynamicDataSourceType.Method)]
        public void Case03PrivateToPublicKey(byte[] privateKey, byte[] expected)
        {
            // Arrange
            var parameters = new BLSParameters()
            {
                PrivateKey = privateKey
            };
            Console.WriteLine("Input:");
            Console.WriteLine("Private Key: [{0}] {1}", privateKey.Length, HexMate.Convert.ToHexString(privateKey));

            // Act
            using var bls = new BLSHerumi(parameters);
            var result = new byte[48];
            var success = bls.TryExportBLSPublicKey(result.AsSpan(), out var bytesWritten);

            Console.WriteLine("Output:");
            Console.WriteLine("Public Key: [{0}] {1}", result.Length, HexMate.Convert.ToHexString(result));

            // Assert
            result.ShouldBe(expected);
        }

        public static IEnumerable<object[]> Case03PrivateToPublicKeyData()
        {
            yield return new object[]
            {
                HexMate.Convert.FromHexString("263dbd792f5b1be47ed85f8938c0f29586af0d3ac7b977f21c278fe1462040e3"),
                HexMate.Convert.FromHexString("a491d1b0ecd9bb917989f0e74f0dea0422eac4a873e5e2644f368dffb9a6e20fd6e10c1b77654d067c0618f6e5a7f79a"),
            };

            yield return new object[]
            {
                HexMate.Convert.FromHexString("47b8192d77bf871b62e87859d653922725724a5c031afeabc60bcef5ff665138"),
                HexMate.Convert.FromHexString("b301803f8b5ac4a1133581fc676dfedc60d891dd5fa99028805e5ea5b08d3491af75d0707adab3b70c6a6a580217bf81"),
            };

            yield return new object[]
            {
                HexMate.Convert.FromHexString("328388aff0d4a5b7dc9205abd374e7e98f3cd9f3418edb4eafda5fb16473d216"),
                HexMate.Convert.FromHexString("b53d21a4cfd562c469cc81514d4ce5a6b577d8403d32a394dc265dd190b47fa9f829fdd7963afdf972e5e77854051f6f"),
            };

            //return PrivateKeys.Select(x => new object[] { HexMate.Convert.FromHexString(x) });
        }

        [DataTestMethod]
        [DynamicData(nameof(Case04SignMessagesData), DynamicDataSourceType.Method)]
        public void Case04SignMessages(byte[] privateKey, byte[] messageHash, byte[] domain, byte[] expected)
        {
            // Arrange
            var parameters = new BLSParameters()
            {
                PrivateKey = privateKey
            };
            Console.WriteLine("Input:");
            Console.WriteLine("Domain: [{0}] {1}", domain.Length, HexMate.Convert.ToHexString(domain));
            Console.WriteLine("MessageHash: [{0}] {1}", messageHash.Length, HexMate.Convert.ToHexString(messageHash));
            Console.WriteLine("Private Key: [{0}] {1}", privateKey.Length, HexMate.Convert.ToHexString(privateKey));

            // Act
            using var bls = new BLSHerumi(parameters);
            var result = new byte[96];
            var success = bls.TrySignHash(messageHash, result.AsSpan(), out var bytesWritten, domain);

            Console.WriteLine("Output:");
            Console.WriteLine("Signature: {0} [{1}] {2}", success, bytesWritten, HexMate.Convert.ToHexString(result));

            // Assert
            result.ShouldBe(expected);
        }

        public static IEnumerable<object[]> Case04SignMessagesData()
        {
            yield return new object[]
            {
                HexMate.Convert.FromHexString("47b8192d77bf871b62e87859d653922725724a5c031afeabc60bcef5ff665138"),
                HexMate.Convert.FromHexString("0000000000000000000000000000000000000000000000000000000000000000"),
                HexMate.Convert.FromHexString("0000000000000000"),
                HexMate.Convert.FromHexString("b9d1bf921b3dd048bdce38c2ceac2a2a8093c864881f2415f22b198de935ffa791707855c1656dc21a7af2d502bb46590151d645f062634c3b2cb79c4ed1c4a4b8b3f19f0f5c76965c651553e83d153ff95353735156eff77692f7a62ae653fb")
            };

            yield return new object[]
            {
                HexMate.Convert.FromHexString("47b8192d77bf871b62e87859d653922725724a5c031afeabc60bcef5ff665138"),
                HexMate.Convert.FromHexString("0000000000000000000000000000000000000000000000000000000000000000"),
                HexMate.Convert.FromHexString("0000000000000001"),
                HexMate.Convert.FromHexString("98e4a692a006b8b9a88186e01bf49cdd415b1676b3c89b9b7e87e8871ff5d7f72738c9744175ca37e777a52be8394d7e10a3f1d2419572399a8b8aa37158be9302edd813dea0331bb4b4321a644a402b939368fcfa67c66e7aba8533a7df632d"),
            };

            yield return new object[]
            {
                HexMate.Convert.FromHexString("47b8192d77bf871b62e87859d653922725724a5c031afeabc60bcef5ff665138"),
                HexMate.Convert.FromHexString("0000000000000000000000000000000000000000000000000000000000000000"),
                HexMate.Convert.FromHexString("0123456789abcdef"),
                HexMate.Convert.FromHexString("91070f6745db83c52cd007d1dd884bbd89e1d2e2ee755f2736c9bf057a20994b1e793284fe121ffc3d79dbf0d3b4a1621844aa56012c79a3d37ad1d40d2da58ba72317798c84ea238051a92e3f140a454ccf3e1c89d439cc58f50ec8abd74660"),
            };

            yield return new object[]
            {
                HexMate.Convert.FromHexString("47b8192d77bf871b62e87859d653922725724a5c031afeabc60bcef5ff665138"),
                HexMate.Convert.FromHexString("0000000000000000000000000000000000000000000000000000000000000000"),
                HexMate.Convert.FromHexString("0100000000000000"),
                HexMate.Convert.FromHexString("9180679405a60fde7f5556165a91730fd5b2501c63e3ff5196b1b293a4d52753c3764319a29976d475ebdc43e963103e0877745a8c4cb61cb22571a85c856d85c01af3bcfc06ff697dfebddaf680cf0f39b10d5b621fcdc2e3bb16a759ec3d12"),
            };

            yield return new object[]
            {
                HexMate.Convert.FromHexString("47b8192d77bf871b62e87859d653922725724a5c031afeabc60bcef5ff665138"),
                HexMate.Convert.FromHexString("0000000000000000000000000000000000000000000000000000000000000000"),
                HexMate.Convert.FromHexString("8000000000000000"),
                HexMate.Convert.FromHexString("931065c857d277ed065e38fe057e1f774534f7e6848a91aa240bf98e9e4eefc8ae07b966d6032d1f05802df5b901a3ea14a45e55e093c7e33b8d99fdb1e226565111c8bb54639e6013ba0dcd4622b0b3db91a0588f056bd4074637c44fe1e3e9"),
            };

            yield return new object[]
            {
                HexMate.Convert.FromHexString("47b8192d77bf871b62e87859d653922725724a5c031afeabc60bcef5ff665138"),
                HexMate.Convert.FromHexString("0000000000000000000000000000000000000000000000000000000000000000"),
                HexMate.Convert.FromHexString("ffffffffffffffff"),
                HexMate.Convert.FromHexString("b3a8aea823c7ab53be02d8b3d80094188be3d99924efaaee6eff9a7ca876710bf72b1b1de4afb32f5acd35bfbf8a447107efc005f04726c9163d1484b2db64badcfe584c540f2c4cc8d3c0d6dd98dc584fe95552de65bcc58bf606e2b87660ff"),
            };

            yield return new object[]
            {
                HexMate.Convert.FromHexString("263dbd792f5b1be47ed85f8938c0f29586af0d3ac7b977f21c278fe1462040e3"),
                HexMate.Convert.FromHexString("0000000000000000000000000000000000000000000000000000000000000000"),
                HexMate.Convert.FromHexString("0000000000000000"),
                HexMate.Convert.FromHexString("97004641c3f3c9973e5d5064578e7b87230655905546f5e95469dfa41cfae49f3741112cdf425f8a3f9d0fdde2f4980516b1fd6748d87a234589f065145cfe9c697cc6a61211c9322ad4c279c20b8d943c8c2f1dd13fc0418cb2dac4d0a9e34d"),
            };

            yield return new object[]
            {
                HexMate.Convert.FromHexString("328388aff0d4a5b7dc9205abd374e7e98f3cd9f3418edb4eafda5fb16473d216"),
                HexMate.Convert.FromHexString("abababababababababababababababababababababababababababababababab"),
                HexMate.Convert.FromHexString("ffffffffffffffff"),
                HexMate.Convert.FromHexString("862a67bd1ea2a92c818a5572e889001836423660232cda8c3bbf365a7111702b45933a9951da59cd9d50d4667e408f770429659e1506ed89d41a8daf0126afc0f53ebc6913e783d941756d156c28fc554843ae8c2b39ed3ab8ea72c6b17e4918"),
            };

            //return PrivateKeys.SelectMany(privateKey =>
            //    MessageHashes.SelectMany(messageHash =>
            //        Domains.Select(domain =>
            //            new object[] {
            //                HexMate.Convert.FromHexString(privateKey),
            //                messageHash,
            //                domain,
            //                new byte[0]
            //            })
            //        )
            //    );
        }

        [DataTestMethod]
        [DynamicData(nameof(Case05VerifyMessagesData), DynamicDataSourceType.Method)]
        public void Case05VerifyMessages(byte[] publicKey, byte[] messageHash, byte[] domain, byte[] signature, bool expected)
        {
            // Arrange
            var parameters = new BLSParameters()
            {
                PublicKey = publicKey
            };
            Console.WriteLine("Input:");
            Console.WriteLine("Public Key: [{0}] {1}", publicKey.Length, HexMate.Convert.ToHexString(publicKey));
            Console.WriteLine("MessageHash: [{0}] {1}", messageHash.Length, HexMate.Convert.ToHexString(messageHash));
            Console.WriteLine("Domain: [{0}] {1}", domain.Length, HexMate.Convert.ToHexString(domain));
            Console.WriteLine("Signature: [{0}] {1}", signature.Length, HexMate.Convert.ToHexString(signature));

            // Act
            using var bls = new BLSHerumi(parameters);
            var success = bls.VerifyHash(messageHash, signature, domain);

            Console.WriteLine("Output: Success {0}", success);

            // Assert
            success.ShouldBe(expected);
        }

        public static IEnumerable<object[]> Case05VerifyMessagesData()
        {
            yield return new object[]
            {
                //HexMate.Convert.FromHexString("47b8192d77bf871b62e87859d653922725724a5c031afeabc60bcef5ff665138"),
                HexMate.Convert.FromHexString("b301803f8b5ac4a1133581fc676dfedc60d891dd5fa99028805e5ea5b08d3491af75d0707adab3b70c6a6a580217bf81"),
                HexMate.Convert.FromHexString("0000000000000000000000000000000000000000000000000000000000000000"),
                HexMate.Convert.FromHexString("0000000000000000"),
                HexMate.Convert.FromHexString("b9d1bf921b3dd048bdce38c2ceac2a2a8093c864881f2415f22b198de935ffa791707855c1656dc21a7af2d502bb46590151d645f062634c3b2cb79c4ed1c4a4b8b3f19f0f5c76965c651553e83d153ff95353735156eff77692f7a62ae653fb"),
                true
            };

            yield return new object[]
            {
                //HexMate.Convert.FromHexString("263dbd792f5b1be47ed85f8938c0f29586af0d3ac7b977f21c278fe1462040e3"),
                HexMate.Convert.FromHexString("a491d1b0ecd9bb917989f0e74f0dea0422eac4a873e5e2644f368dffb9a6e20fd6e10c1b77654d067c0618f6e5a7f79a"),
                HexMate.Convert.FromHexString("0000000000000000000000000000000000000000000000000000000000000000"),
                HexMate.Convert.FromHexString("0000000000000000"),
                HexMate.Convert.FromHexString("97004641c3f3c9973e5d5064578e7b87230655905546f5e95469dfa41cfae49f3741112cdf425f8a3f9d0fdde2f4980516b1fd6748d87a234589f065145cfe9c697cc6a61211c9322ad4c279c20b8d943c8c2f1dd13fc0418cb2dac4d0a9e34d"),
                true
            };

            yield return new object[]
            {
                //HexMate.Convert.FromHexString("328388aff0d4a5b7dc9205abd374e7e98f3cd9f3418edb4eafda5fb16473d216"),
                HexMate.Convert.FromHexString("b53d21a4cfd562c469cc81514d4ce5a6b577d8403d32a394dc265dd190b47fa9f829fdd7963afdf972e5e77854051f6f"),
                HexMate.Convert.FromHexString("abababababababababababababababababababababababababababababababab"),
                HexMate.Convert.FromHexString("ffffffffffffffff"),
                HexMate.Convert.FromHexString("862a67bd1ea2a92c818a5572e889001836423660232cda8c3bbf365a7111702b45933a9951da59cd9d50d4667e408f770429659e1506ed89d41a8daf0126afc0f53ebc6913e783d941756d156c28fc554843ae8c2b39ed3ab8ea72c6b17e4918"),
                true
            };

            yield return new object[]
            {
                HexMate.Convert.FromHexString("b301803f8b5ac4a1133581fc676dfedc60d891dd5fa99028805e5ea5b08d3491af75d0707adab3b70c6a6a580217bf81"),
                HexMate.Convert.FromHexString("0000000000000000000000000000000000000000000000000000000000000000"),
                HexMate.Convert.FromHexString("0000000000000000"),
                // Signature from a different test case
                HexMate.Convert.FromHexString("862a67bd1ea2a92c818a5572e889001836423660232cda8c3bbf365a7111702b45933a9951da59cd9d50d4667e408f770429659e1506ed89d41a8daf0126afc0f53ebc6913e783d941756d156c28fc554843ae8c2b39ed3ab8ea72c6b17e4918"),
                false
            };
        }

        [DataTestMethod]
        [DynamicData(nameof(Case06AggregateSignaturesData), DynamicDataSourceType.Method)]
        public void Case06AggregateSignatures(byte[][] signatures, byte[] expected)
        {
            // Arrange
            var inputSpan = new Span<byte>(new byte[signatures.Length * 96]);
            Console.WriteLine("Input: [{0}]", signatures.Length);
            for (var index = 0; index < signatures.Length; index++)
            {
                signatures[index].CopyTo(inputSpan.Slice(index * 96));
                Console.WriteLine("Signature({0}): [{1}] {2}", index, signatures[index].Length, HexMate.Convert.ToHexString(signatures[index]));
            }

            // Act
            using var bls = new BLSHerumi(new BLSParameters());
            var result = new byte[96];
            var success = bls.TryAggregateSignatures(inputSpan, result, out var bytesWritten);

            Console.WriteLine("Output:");
            Console.WriteLine("Combined: {0} [{1}] {2}", success, bytesWritten, HexMate.Convert.ToHexString(result));

            // Assert
            result.ShouldBe(expected);
        }

        public static IEnumerable<object[]> Case06AggregateSignaturesData()
        {
            yield return new object[]
            {
                new byte[][] {
                    HexMate.Convert.FromHexString("97004641c3f3c9973e5d5064578e7b87230655905546f5e95469dfa41cfae49f3741112cdf425f8a3f9d0fdde2f4980516b1fd6748d87a234589f065145cfe9c697cc6a61211c9322ad4c279c20b8d943c8c2f1dd13fc0418cb2dac4d0a9e34d"),
                    HexMate.Convert.FromHexString("b9d1bf921b3dd048bdce38c2ceac2a2a8093c864881f2415f22b198de935ffa791707855c1656dc21a7af2d502bb46590151d645f062634c3b2cb79c4ed1c4a4b8b3f19f0f5c76965c651553e83d153ff95353735156eff77692f7a62ae653fb"),
                    HexMate.Convert.FromHexString("a234bc1a0acd31b3a8758247a0ea5fbf1a08a5ecdf82037993ec5bfd68836ac01b966b025e55c100e30d18adb327710315b9d4905f108b4be9fb58f7be5690f5c061de2e02fca47f79202bb8ddfa64d32aa0914384941f13f400c5eb1b96965f"),
                },
                HexMate.Convert.FromHexString("a02d658fa52328a088903713509db8faaeaf0daf68415df055fe4f1fa0d848bd8ea6428f9852d4f7f771b496f0e24930106e589fa1dd89f0d2263431f03cdf3950df96f075e89255c909ba8862f5ae406e08a9d46e885ceee3d8863b1d5e6507"),
            };

            yield return new object[]
            {
                new byte[][] {
                    HexMate.Convert.FromHexString("89c3c5d4f8eca4a7f9a9345200b42830c3cdf5cfa4ed3a821c7408d161d55731551a043bf634be5682772d0b9049776804327bcac3ba43a8caf80b19685e85668997c2f8390a805155e752727421bf73651cbd09289c6444a0aaece96f747d27"),
                    HexMate.Convert.FromHexString("b3a8aea823c7ab53be02d8b3d80094188be3d99924efaaee6eff9a7ca876710bf72b1b1de4afb32f5acd35bfbf8a447107efc005f04726c9163d1484b2db64badcfe584c540f2c4cc8d3c0d6dd98dc584fe95552de65bcc58bf606e2b87660ff"),
                    HexMate.Convert.FromHexString("879143289b766dd2adca59a597aadfb87748976f321d47e1892571272af12ea6f3916945ef9a6d80448e47f1d81dbbb513dc73e40e96a0c5601a521d79251c0a03183747f5d9ce78bfe23f77c3c7b33d8b0d2515ce6b935fed3277547542425a"),
                },
                HexMate.Convert.FromHexString("b3087e904c5205bb9a319af70a3aef7a19b74be05407db01a1aa0fad32e915ee2fd1fcb03d649fb90d41f0e6bb9faf32190963e25c98a865cf1851e262554ec7c7a8a2ebb7f27fe2278b544dca30cadb0e5cfcefb87244104e3daf952e5af476"),
            };

            yield return new object[]
            {
                new byte[][] {
                    HexMate.Convert.FromHexString("a8715f2ac11675cf105426368a22a2304d9960a82366c63f96d2e56edf1fda04ba3c6e3d99794d0ead42c2936b24b4f20075d4ccb7edbdff8ae018169a7990b878bb25dda59cb828ab65d3a828bc29b4e0732b7dda1121a6377a34b717733dfd"),
                    HexMate.Convert.FromHexString("93f2210d2eb75824fd2c27bc2ad8527cc2bbf1db8d3fb707e909831955f2172b204d6743150c56295a86089f437d32920576f462d28ef53ccbe45ac1c96a50427b29b4c7c4f89a92686db6be2dfdb0ea44e1442ba3be01ed37e61fafb519a9ec"),
                    HexMate.Convert.FromHexString("b28848b57e3004f17839bce988aecceb4b5dee566d392d8b9f9e7b57c2295fbde9bf102eeff1a89a6bc8bf8b2dc1405c0c8b0c9bea51a8741c6973f918bcd1421a8d4ced2ffeecb9430f7cedf6883809b3a9bcd866956c455682b6d480396a69"),
                },
                HexMate.Convert.FromHexString("b388c574412b766fba799969e1c478a45319041a811e56519c372bae6584d91d214564802e6fa98950f6acb78f5283e10e3e51a85a6a6d546f3d34a6b884a1c6812708e42c548f4fb7908b4130e32e1a39e112b0d838105f79b358e630e6e74a"),
            };

            yield return new object[]
            {
                new byte[][] {
                    HexMate.Convert.FromHexString("abd8f756eda91a448ea8e9ffd1f31d082e63b2848242192bd2c1e1fcae95613702ed03181118c8ba8440a580bef8780f194f196877819998efb85f93c13e43da16cd12a7f3ef99897dede35c0ae1a8f8d66760c2feb201a76111ba5b935945eb"),
                    HexMate.Convert.FromHexString("b6e6d8aa224c5edc502680fae130ac1eb1e769f18b73eee4bafb782c6c244afb8517df2007de038c8b301472543a3eb013e9291ec216d23cee57b8f75d88ab62b27f0902e8d5d96e6d43c4253b76e599983e2ccc33a259f6f74778f2063ef79e"),
                    HexMate.Convert.FromHexString("b39d994a77996c719141d0b2cc2d9184dca9c1369a58265befccf7f36c82ee4be93e623257a329c6426533354aa4d8f60fc1d41b7e56b8fbb1bc118a275d867b432e6bed43cb2f3ddbf62c4a666f3ff042c235a9c7b89d515373ea0f2a0e4e47"),
                },
                HexMate.Convert.FromHexString("ab9f94aae5846301760be418a6f0ccb96178fbfab15ad50755da993c2d1ea4278638eb5157002f0f31824e42d83d6eb018271f6f28f27582bc2dd54820a74af898c6e6ab4cc24368465f0af6026501896f2dbc3f26a55ff0bce2dc378b070e08"),
            };
        }
    }
}
