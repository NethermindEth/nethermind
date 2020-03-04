// //  Copyright (c) 2018 Demerzel Solutions Limited
// //  This file is part of the Nethermind library.
// // 
// //  The Nethermind library is free software: you can redistribute it and/or modify
// //  it under the terms of the GNU Lesser General Public License as published by
// //  the Free Software Foundation, either version 3 of the License, or
// //  (at your option) any later version.
// // 
// //  The Nethermind library is distributed in the hope that it will be useful,
// //  but WITHOUT ANY WARRANTY; without even the implied warranty of
// //  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// //  GNU Lesser General Public License for more details.
// // 
// //  You should have received a copy of the GNU Lesser General Public License
// //  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//
// using System;
// using System.Collections.Generic;
// using System.Linq;
// using Microsoft.VisualStudio.TestTools.UnitTesting;
// using Shouldly;
//
// namespace Nethermind.Cryptography.Bls.Test
// {
//     [TestClass]
//     public class BlsOldSignHashTest
//     {
//         // Check against other test suites, e.g. Artemis
//         //input: {privkey: '0x47b8192d77bf871b62e87859d653922725724a5c031afeabc60bcef5ff665138',
//         //message: '0x0000000000000000000000000000000000000000000000000000000000000000', domain: '0x0000000000000000'}
//         //  output: '0xb9d1bf921b3dd048bdce38c2ceac2a2a8093c864881f2415f22b198de935ffa791707855c1656dc21a7af2d502bb46590151d645f062634c3b2cb79c4ed1c4a4b8b3f19f0f5c76965c651553e83d153ff95353735156eff77692f7a62ae653fb'
//
//         // Older results (seem to be different):
//         // https://gist.github.com/ChihChengLiang/328bc1db5d2a47950e5364c11f23052a
//
//         [DataTestMethod]
//         [DynamicData(nameof(Case04SignHashData), DynamicDataSourceType.Method)]
//         public void Case04SignHash(byte[] privateKey, byte[] messageHash, byte[] domain, byte[] expected)
//         {
//             // Arrange
//             var parameters = new BLSParameters()
//             {
//                 PrivateKey = privateKey
//             };
//             Console.WriteLine("Input:");
//             Console.WriteLine("Domain: [{0}] {1}", domain.Length, HexMate.Convert.ToHexString(domain));
//             Console.WriteLine("MessageHash: [{0}] {1}", messageHash.Length, HexMate.Convert.ToHexString(messageHash));
//             Console.WriteLine("Private Key: [{0}] {1}", privateKey.Length, HexMate.Convert.ToHexString(privateKey));
//
//             // Act
//             using var bls = new BLSHerumi(parameters);
//             var result = new byte[96];
//             var success = bls.TrySignHash(messageHash, result.AsSpan(), out var bytesWritten, domain);
//
//             Console.WriteLine("Output:");
//             Console.WriteLine("Signature: {0} [{1}] {2}", success, bytesWritten, HexMate.Convert.ToHexString(result));
//
//             // Assert
//             result.ShouldBe(expected);
//         }
//
//         public static IEnumerable<object[]> Case04SignHashData()
//         {
//             yield return new object[]
//             {
//                 HexMate.Convert.FromHexString("47b8192d77bf871b62e87859d653922725724a5c031afeabc60bcef5ff665138"),
//                 HexMate.Convert.FromHexString("0000000000000000000000000000000000000000000000000000000000000000"),
//                 HexMate.Convert.FromHexString("0000000000000000"),
//                 HexMate.Convert.FromHexString("b9d1bf921b3dd048bdce38c2ceac2a2a8093c864881f2415f22b198de935ffa791707855c1656dc21a7af2d502bb46590151d645f062634c3b2cb79c4ed1c4a4b8b3f19f0f5c76965c651553e83d153ff95353735156eff77692f7a62ae653fb")
//             };
//
//             yield return new object[]
//             {
//                 HexMate.Convert.FromHexString("47b8192d77bf871b62e87859d653922725724a5c031afeabc60bcef5ff665138"),
//                 HexMate.Convert.FromHexString("0000000000000000000000000000000000000000000000000000000000000000"),
//                 HexMate.Convert.FromHexString("0000000000000001"),
//                 HexMate.Convert.FromHexString("98e4a692a006b8b9a88186e01bf49cdd415b1676b3c89b9b7e87e8871ff5d7f72738c9744175ca37e777a52be8394d7e10a3f1d2419572399a8b8aa37158be9302edd813dea0331bb4b4321a644a402b939368fcfa67c66e7aba8533a7df632d"),
//             };
//
//             yield return new object[]
//             {
//                 HexMate.Convert.FromHexString("47b8192d77bf871b62e87859d653922725724a5c031afeabc60bcef5ff665138"),
//                 HexMate.Convert.FromHexString("0000000000000000000000000000000000000000000000000000000000000000"),
//                 HexMate.Convert.FromHexString("0123456789abcdef"),
//                 HexMate.Convert.FromHexString("91070f6745db83c52cd007d1dd884bbd89e1d2e2ee755f2736c9bf057a20994b1e793284fe121ffc3d79dbf0d3b4a1621844aa56012c79a3d37ad1d40d2da58ba72317798c84ea238051a92e3f140a454ccf3e1c89d439cc58f50ec8abd74660"),
//             };
//
//             yield return new object[]
//             {
//                 HexMate.Convert.FromHexString("47b8192d77bf871b62e87859d653922725724a5c031afeabc60bcef5ff665138"),
//                 HexMate.Convert.FromHexString("0000000000000000000000000000000000000000000000000000000000000000"),
//                 HexMate.Convert.FromHexString("0100000000000000"),
//                 HexMate.Convert.FromHexString("9180679405a60fde7f5556165a91730fd5b2501c63e3ff5196b1b293a4d52753c3764319a29976d475ebdc43e963103e0877745a8c4cb61cb22571a85c856d85c01af3bcfc06ff697dfebddaf680cf0f39b10d5b621fcdc2e3bb16a759ec3d12"),
//             };
//
//             yield return new object[]
//             {
//                 HexMate.Convert.FromHexString("47b8192d77bf871b62e87859d653922725724a5c031afeabc60bcef5ff665138"),
//                 HexMate.Convert.FromHexString("0000000000000000000000000000000000000000000000000000000000000000"),
//                 HexMate.Convert.FromHexString("8000000000000000"),
//                 HexMate.Convert.FromHexString("931065c857d277ed065e38fe057e1f774534f7e6848a91aa240bf98e9e4eefc8ae07b966d6032d1f05802df5b901a3ea14a45e55e093c7e33b8d99fdb1e226565111c8bb54639e6013ba0dcd4622b0b3db91a0588f056bd4074637c44fe1e3e9"),
//             };
//
//             yield return new object[]
//             {
//                 HexMate.Convert.FromHexString("47b8192d77bf871b62e87859d653922725724a5c031afeabc60bcef5ff665138"),
//                 HexMate.Convert.FromHexString("0000000000000000000000000000000000000000000000000000000000000000"),
//                 HexMate.Convert.FromHexString("ffffffffffffffff"),
//                 HexMate.Convert.FromHexString("b3a8aea823c7ab53be02d8b3d80094188be3d99924efaaee6eff9a7ca876710bf72b1b1de4afb32f5acd35bfbf8a447107efc005f04726c9163d1484b2db64badcfe584c540f2c4cc8d3c0d6dd98dc584fe95552de65bcc58bf606e2b87660ff"),
//             };
//
//             yield return new object[]
//             {
//                 HexMate.Convert.FromHexString("263dbd792f5b1be47ed85f8938c0f29586af0d3ac7b977f21c278fe1462040e3"),
//                 HexMate.Convert.FromHexString("0000000000000000000000000000000000000000000000000000000000000000"),
//                 HexMate.Convert.FromHexString("0000000000000000"),
//                 HexMate.Convert.FromHexString("97004641c3f3c9973e5d5064578e7b87230655905546f5e95469dfa41cfae49f3741112cdf425f8a3f9d0fdde2f4980516b1fd6748d87a234589f065145cfe9c697cc6a61211c9322ad4c279c20b8d943c8c2f1dd13fc0418cb2dac4d0a9e34d"),
//             };
//
//             yield return new object[]
//             {
//                 HexMate.Convert.FromHexString("328388aff0d4a5b7dc9205abd374e7e98f3cd9f3418edb4eafda5fb16473d216"),
//                 HexMate.Convert.FromHexString("abababababababababababababababababababababababababababababababab"),
//                 HexMate.Convert.FromHexString("ffffffffffffffff"),
//                 HexMate.Convert.FromHexString("862a67bd1ea2a92c818a5572e889001836423660232cda8c3bbf365a7111702b45933a9951da59cd9d50d4667e408f770429659e1506ed89d41a8daf0126afc0f53ebc6913e783d941756d156c28fc554843ae8c2b39ed3ab8ea72c6b17e4918"),
//             };
//
//             //return PrivateKeys.SelectMany(privateKey =>
//             //    MessageHashes.SelectMany(messageHash =>
//             //        Domains.Select(domain =>
//             //            new object[] {
//             //                HexMate.Convert.FromHexString(privateKey),
//             //                messageHash,
//             //                domain,
//             //                new byte[0]
//             //            })
//             //        )
//             //    );
//         }
//
//         [DataTestMethod]
//         [DynamicData(nameof(Case05VerifyHashData), DynamicDataSourceType.Method)]
//         public void Case05VerifyHash(byte[] publicKey, byte[] messageHash, byte[] domain, byte[] signature, bool expected)
//         {
//             // Arrange
//             var parameters = new BLSParameters()
//             {
//                 PublicKey = publicKey
//             };
//             Console.WriteLine("Input:");
//             Console.WriteLine("Public Key: [{0}] {1}", publicKey.Length, HexMate.Convert.ToHexString(publicKey));
//             Console.WriteLine("MessageHash: [{0}] {1}", messageHash.Length, HexMate.Convert.ToHexString(messageHash));
//             Console.WriteLine("Domain: [{0}] {1}", domain.Length, HexMate.Convert.ToHexString(domain));
//             Console.WriteLine("Signature: [{0}] {1}", signature.Length, HexMate.Convert.ToHexString(signature));
//
//             // Act
//             using var bls = new BLSHerumi(parameters);
//             var success = bls.VerifyHash(messageHash, signature, domain);
//
//             Console.WriteLine("Output: Success {0}", success);
//
//             // Assert
//             success.ShouldBe(expected);
//         }
//
//         public static IEnumerable<object[]> Case05VerifyHashData()
//         {
//             yield return new object[]
//             {
//                 //HexMate.Convert.FromHexString("47b8192d77bf871b62e87859d653922725724a5c031afeabc60bcef5ff665138"),
//                 HexMate.Convert.FromHexString("b301803f8b5ac4a1133581fc676dfedc60d891dd5fa99028805e5ea5b08d3491af75d0707adab3b70c6a6a580217bf81"),
//                 HexMate.Convert.FromHexString("0000000000000000000000000000000000000000000000000000000000000000"),
//                 HexMate.Convert.FromHexString("0000000000000000"),
//                 HexMate.Convert.FromHexString("b9d1bf921b3dd048bdce38c2ceac2a2a8093c864881f2415f22b198de935ffa791707855c1656dc21a7af2d502bb46590151d645f062634c3b2cb79c4ed1c4a4b8b3f19f0f5c76965c651553e83d153ff95353735156eff77692f7a62ae653fb"),
//                 true
//             };
//
//             yield return new object[]
//             {
//                 //HexMate.Convert.FromHexString("263dbd792f5b1be47ed85f8938c0f29586af0d3ac7b977f21c278fe1462040e3"),
//                 HexMate.Convert.FromHexString("a491d1b0ecd9bb917989f0e74f0dea0422eac4a873e5e2644f368dffb9a6e20fd6e10c1b77654d067c0618f6e5a7f79a"),
//                 HexMate.Convert.FromHexString("0000000000000000000000000000000000000000000000000000000000000000"),
//                 HexMate.Convert.FromHexString("0000000000000000"),
//                 HexMate.Convert.FromHexString("97004641c3f3c9973e5d5064578e7b87230655905546f5e95469dfa41cfae49f3741112cdf425f8a3f9d0fdde2f4980516b1fd6748d87a234589f065145cfe9c697cc6a61211c9322ad4c279c20b8d943c8c2f1dd13fc0418cb2dac4d0a9e34d"),
//                 true
//             };
//
//             yield return new object[]
//             {
//                 //HexMate.Convert.FromHexString("328388aff0d4a5b7dc9205abd374e7e98f3cd9f3418edb4eafda5fb16473d216"),
//                 HexMate.Convert.FromHexString("b53d21a4cfd562c469cc81514d4ce5a6b577d8403d32a394dc265dd190b47fa9f829fdd7963afdf972e5e77854051f6f"),
//                 HexMate.Convert.FromHexString("abababababababababababababababababababababababababababababababab"),
//                 HexMate.Convert.FromHexString("ffffffffffffffff"),
//                 HexMate.Convert.FromHexString("862a67bd1ea2a92c818a5572e889001836423660232cda8c3bbf365a7111702b45933a9951da59cd9d50d4667e408f770429659e1506ed89d41a8daf0126afc0f53ebc6913e783d941756d156c28fc554843ae8c2b39ed3ab8ea72c6b17e4918"),
//                 true
//             };
//
//             yield return new object[]
//             {
//                 HexMate.Convert.FromHexString("b301803f8b5ac4a1133581fc676dfedc60d891dd5fa99028805e5ea5b08d3491af75d0707adab3b70c6a6a580217bf81"),
//                 HexMate.Convert.FromHexString("0000000000000000000000000000000000000000000000000000000000000000"),
//                 HexMate.Convert.FromHexString("0000000000000000"),
//                 // Signature from a different test case
//                 HexMate.Convert.FromHexString("862a67bd1ea2a92c818a5572e889001836423660232cda8c3bbf365a7111702b45933a9951da59cd9d50d4667e408f770429659e1506ed89d41a8daf0126afc0f53ebc6913e783d941756d156c28fc554843ae8c2b39ed3ab8ea72c6b17e4918"),
//                 false
//             };
//         }
//     }
// }
