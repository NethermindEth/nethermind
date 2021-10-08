//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace Nethermind.Cryptography.Bls.Test
{
    [TestClass]
    public class SpecSampleTests
    {
        // NOTE: This is not every test from the spec, just a sample of them for initial checking
     
                [DataTestMethod]
        [DynamicData(nameof(SignTestData), DynamicDataSourceType.Method)]
        public void Sign(string testName, byte[] privateKey, byte[] messageData, byte[] expected)
        {
            // Arrange
            var parameters = new BLSParameters()
            {
                PrivateKey = privateKey
            };
            Console.WriteLine("Input:");
            Console.WriteLine("MessageData: [{0}] {1}", messageData.Length, HexMate.Convert.ToHexString(messageData));
            Console.WriteLine("Private Key: [{0}] {1}", privateKey.Length, HexMate.Convert.ToHexString(privateKey));

            // Act
            using var bls = new BLSHerumi(parameters);
            var result = new byte[96];
            //var success = bls.TrySignData(privateKey, messageData, result.AsSpan(), out var bytesWritten);
            var success = bls.TrySignData(messageData, result.AsSpan(), out var bytesWritten);

            Console.WriteLine("Output:");
            Console.WriteLine("Signature: {0} [{1}] {2}", success, bytesWritten, HexMate.Convert.ToHexString(result));

            // Assert
            result.ShouldBe(expected);
        }

        public static IEnumerable<object[]> SignTestData()
        {
            yield return new object[]
            {
                "sign_case_8cd3d4d0d9a5b265",
                HexMate.Convert.FromHexString("328388aff0d4a5b7dc9205abd374e7e98f3cd9f3418edb4eafda5fb16473d216"),
                HexMate.Convert.FromHexString("5656565656565656565656565656565656565656565656565656565656565656"),
                HexMate.Convert.FromHexString(
                    "89dcc02150631de23c5ba6fac74394163f1f05643c77e0bde7fea29ce64cf5fe68c440ff401908d81cc3ddcd46db41cf119e6ab5f897cafdb9b78000437354ea9796b61badc28e6d757e42c0dd7e55bd5b4fd4d9a694ddbddb5f524511090277")
            };

            yield return new object[]
            {
                "sign_case_11b8c7cad5238946",
                HexMate.Convert.FromHexString("47b8192d77bf871b62e87859d653922725724a5c031afeabc60bcef5ff665138"),
                HexMate.Convert.FromHexString("0000000000000000000000000000000000000000000000000000000000000000"),
                HexMate.Convert.FromHexString(
                    "b2deb7c656c86cb18c43dae94b21b107595486438e0b906f3bdb29fa316d0fc3cab1fc04c6ec9879c773849f2564d39317bfa948b4a35fc8509beafd3a2575c25c077ba8bca4df06cb547fe7ca3b107d49794b7132ef3b5493a6ffb2aad2a441")
            };
        }

        [DataTestMethod]
        [DynamicData(nameof(VerifyTestData), DynamicDataSourceType.Method)]
        public void Verify(string testName, byte[] publicKey, byte[] messageData, byte[] signature, bool expected)
        {
            // Arrange
            var parameters = new BLSParameters()
            {
                PublicKey = publicKey
            };
            Console.WriteLine("Input:");
            Console.WriteLine("Public Key: [{0}] {1}", publicKey.Length, HexMate.Convert.ToHexString(publicKey));
            Console.WriteLine("MessageHash: [{0}] {1}", messageData.Length, HexMate.Convert.ToHexString(messageData));
            Console.WriteLine("Signature: [{0}] {1}", signature.Length, HexMate.Convert.ToHexString(signature));

            // Act
            using var bls = new BLSHerumi(parameters);
            var success = bls.VerifyData(messageData, signature);

            Console.WriteLine("Output: Success {0}", success);

            // Assert
            success.ShouldBe(expected);
        }

        public static IEnumerable<object[]> VerifyTestData()
        {
            yield return new object[]
            {
                "verify_case_0ddce33eac2eea2d",
                HexMate.Convert.FromHexString(
                    "b53d21a4cfd562c469cc81514d4ce5a6b577d8403d32a394dc265dd190b47fa9f829fdd7963afdf972e5e77854051f6f"),
                HexMate.Convert.FromHexString("0000000000000000000000000000000000000000000000000000000000000000"),
                HexMate.Convert.FromHexString(
                    "b2deb7c656c86cb18c43dae94b21b107595486438e0b906f3bdb29fa316d0fc3cab1fc04c6ec9879c773849f2564d39317bfa948b4a35fc8509beafd3a2575c25c077ba8bca4df06cb547fe7ca3b107d49794b7132ef3b5493a6ffb2aad2a441"),
                false
            };

            yield return new object[]
            {
                "verify_case_4cabb1d0b2db66a6",
                HexMate.Convert.FromHexString(
                "b53d21a4cfd562c469cc81514d4ce5a6b577d8403d32a394dc265dd190b47fa9f829fdd7963afdf972e5e77854051f6f"),
                HexMate.Convert.FromHexString("5656565656565656565656565656565656565656565656565656565656565656"),
                HexMate.Convert.FromHexString(
                    "89dcc02150631de23c5ba6fac74394163f1f05643c77e0bde7fea29ce64cf5fe68c440ff401908d81cc3ddcd46db41cf119e6ab5f897cafdb9b78000437354ea9796b61badc28e6d757e42c0dd7e55bd5b4fd4d9a694ddbddb5f524511090277"),
                true
            };
        }

    }
}