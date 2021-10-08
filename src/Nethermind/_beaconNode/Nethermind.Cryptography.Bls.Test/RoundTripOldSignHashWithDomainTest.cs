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
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace Nethermind.Cryptography.Bls.Test
{
    [TestClass]
    public class RoundTripOldSignHashWithDomainTest
    {
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
        
        // [TestMethod]
        // public void BlsRoundtripSignAndVerify()
        // {
        //     // Arrange
        //     var privateKey = HexMate.Convert.FromHexString(PrivateKeys[1]);
        //     var messageHash = MessageHashes[0];
        //     var domain = Domains[0];
        //
        //     Console.WriteLine("Input:");
        //     Console.WriteLine("Private Key: [{0}] {1}", privateKey.Length, HexMate.Convert.ToHexString(privateKey));
        //     Console.WriteLine("Domain: [{0}] {1}", domain.Length, HexMate.Convert.ToHexString(domain));
        //     Console.WriteLine("MessageHash: [{0}] {1}", messageHash.Length, HexMate.Convert.ToHexString(messageHash));
        //
        //     // Act
        //     var parameters = new BLSParameters()
        //     {
        //         PrivateKey = privateKey
        //     };
        //     using var bls = new BLSHerumi(parameters);
        //
        //     var publicKey = new byte[48];
        //     _ = bls.TryExportBlsPublicKey(publicKey, out var _);
        //
        //     Console.WriteLine("Public Key: [{0}] {1}", publicKey.Length, HexMate.Convert.ToHexString(publicKey));
        //
        //     var initialX = new byte[96];
        //     _ = bls.TryCombineHashAndDomain(messageHash, domain, initialX, out var _);
        //
        //     Console.WriteLine("InitialX: [{0}] {1}", initialX.Length, HexMate.Convert.ToHexString(initialX));
        //
        //     var signature = new byte[96];
        //     var signatureSuccess = bls.TrySignHash(initialX, signature.AsSpan(), out var bytesWritten);
        //
        //     Console.WriteLine("Signature: {0} [{1}] {2}", signatureSuccess, bytesWritten, HexMate.Convert.ToHexString(signature));
        //
        //     //var expectedSignature = HexMate.Convert.FromHexString("b9d1bf921b3dd048bdce38c2ceac2a2a8093c864881f2415f22b198de935ffa791707855c1656dc21a7af2d502bb46590151d645f062634c3b2cb79c4ed1c4a4b8b3f19f0f5c76965c651553e83d153ff95353735156eff77692f7a62ae653fb");
        //     //signature.ShouldBe(expectedSignature);
        //
        //     var verifySuccess = bls.VerifyHash(initialX, signature);
        //     Console.WriteLine("Verify1: {0}", verifySuccess);
        //
        //     var parameters2 = new BLSParameters()
        //     {
        //         PublicKey = publicKey
        //     };
        //     using var bls2 = new BLSHerumi(parameters);
        //
        //     var verifySuccess2 = bls2.VerifyHash(initialX, signature);
        //     Console.WriteLine("Verify2: {0}", verifySuccess2);
        //
        //     verifySuccess2.ShouldBeTrue();
        // }

        [TestMethod]
        public void BlsRoundtripSignAndAggregateVerify()
        {
            // Arrange
            var privateKey1 = HexMate.Convert.FromHexString(PrivateKeys[1]);
            var privateKey2 = HexMate.Convert.FromHexString(PrivateKeys[2]);
            var messageHash1 = MessageHashes[1];
            var messageHash2 = MessageHashes[2];
            var domain1 = Domains[1];

            Console.WriteLine("Input:");
            Console.WriteLine("Private Key 1: [{0}] {1}", privateKey1.Length, HexMate.Convert.ToHexString(privateKey1));
            Console.WriteLine("MessageHash 1: [{0}] {1}", messageHash1.Length, HexMate.Convert.ToHexString(messageHash1));
            Console.WriteLine("Private Key 2: [{0}] {1}", privateKey2.Length, HexMate.Convert.ToHexString(privateKey2));
            Console.WriteLine("MessageHash 2: [{0}] {1}", messageHash2.Length, HexMate.Convert.ToHexString(messageHash2));
            Console.WriteLine("Domain: [{0}] {1}", domain1.Length, HexMate.Convert.ToHexString(domain1));

            // Sign 1
            using var bls1 = new BLSHerumi(new BLSParameters() { PrivateKey = privateKey1 });
            var signature1 = new byte[96];
            _ = bls1.TrySignHash(messageHash1, signature1.AsSpan(), out var _, domain1);
            Console.WriteLine("Signature 1: [{0}] {1}", signature1.Length, HexMate.Convert.ToHexString(signature1));
            var publicKey1 = new byte[48];
            _ = bls1.TryExportBlsPublicKey(publicKey1, out var _);
            Console.WriteLine("Public Key 1: [{0}] {1}", publicKey1.Length, HexMate.Convert.ToHexString(publicKey1));

            // Sign 2
            using var bls2 = new BLSHerumi(new BLSParameters() { PrivateKey = privateKey2 });
            var signature2 = new byte[96];
            _ = bls2.TrySignHash(messageHash2, signature2.AsSpan(), out var _, domain1);
            Console.WriteLine("Signature 2: [{0}] {1}", signature2.Length, HexMate.Convert.ToHexString(signature2));
            var publicKey2 = new byte[48];
            _ = bls2.TryExportBlsPublicKey(publicKey2, out var _);
            Console.WriteLine("Public Key 2: [{0}] {1}", publicKey2.Length, HexMate.Convert.ToHexString(publicKey2));

            // Aggregate signatures
            var signatures = new Span<byte>(new byte[96 * 2]);
            signature1.CopyTo(signatures);
            signature2.CopyTo(signatures.Slice(96));
            using var blsAggregate = new BLSHerumi(new BLSParameters());
            var aggregateSignature = new byte[96];
            blsAggregate.TryAggregateSignatures(signatures, aggregateSignature, out var _);
            Console.WriteLine("Aggregate Signature: [{0}] {1}", aggregateSignature.Length, HexMate.Convert.ToHexString(aggregateSignature));

            // Aggregate verify
            using var blsVerify = new BLSHerumi(new BLSParameters());
            var publicKeys = new Span<byte>(new byte[48 * 2]);
            publicKey1.CopyTo(publicKeys);
            publicKey2.CopyTo(publicKeys.Slice(48));
            var hashes = new Span<byte>(new byte[32 * 2]);
            messageHash1.CopyTo(hashes);
            messageHash2.CopyTo(hashes.Slice(32));
            var verifySuccess = blsVerify.AggregateVerifyHashes(publicKeys, hashes, aggregateSignature, domain1);
            Console.WriteLine("Verify: {0}", verifySuccess);

            verifySuccess.ShouldBeTrue();
        }


        [TestMethod]
        public void BlsRoundtripAggregatePublicKeyVerifySharedHash()
        {
            // Arrange
            var privateKey1 = HexMate.Convert.FromHexString(PrivateKeys[1]);
            var privateKey2 = HexMate.Convert.FromHexString(PrivateKeys[2]);
            var sharedMessageHash = MessageHashes[1];
            var domain1 = Domains[1];

            Console.WriteLine("Input:");
            Console.WriteLine("Private Key 1: [{0}] {1}", privateKey1.Length, HexMate.Convert.ToHexString(privateKey1));
            Console.WriteLine("Private Key 2: [{0}] {1}", privateKey2.Length, HexMate.Convert.ToHexString(privateKey2));
            Console.WriteLine("MessageHash 1: [{0}] {1}", sharedMessageHash.Length, HexMate.Convert.ToHexString(sharedMessageHash));
            Console.WriteLine("Domain: [{0}] {1}", domain1.Length, HexMate.Convert.ToHexString(domain1));

            // Sign 1
            using var bls1 = new BLSHerumi(new BLSParameters() { PrivateKey = privateKey1 });
            var signature1 = new byte[96];
            _ = bls1.TrySignHash(sharedMessageHash, signature1.AsSpan(), out var _, domain1);
            Console.WriteLine("Signature 1: [{0}] {1}", signature1.Length, HexMate.Convert.ToHexString(signature1));
            var publicKey1 = new byte[48];
            _ = bls1.TryExportBlsPublicKey(publicKey1, out var _);
            Console.WriteLine("Public Key 1: [{0}] {1}", publicKey1.Length, HexMate.Convert.ToHexString(publicKey1));

            // Sign 2
            using var bls2 = new BLSHerumi(new BLSParameters() { PrivateKey = privateKey2 });
            var signature2 = new byte[96];
            _ = bls2.TrySignHash(sharedMessageHash, signature2.AsSpan(), out var _, domain1);
            Console.WriteLine("Signature 2: [{0}] {1}", signature2.Length, HexMate.Convert.ToHexString(signature2));
            var publicKey2 = new byte[48];
            _ = bls2.TryExportBlsPublicKey(publicKey2, out var _);
            Console.WriteLine("Public Key 2: [{0}] {1}", publicKey2.Length, HexMate.Convert.ToHexString(publicKey2));

            // Aggregate public keys
            var publicKeys = new Span<byte>(new byte[48 * 2]);
            publicKey1.CopyTo(publicKeys);
            publicKey2.CopyTo(publicKeys.Slice(48));
            using var blsAggregateKeys = new BLSHerumi(new BLSParameters());
            var aggregatePublicKey = new byte[48];
            blsAggregateKeys.TryAggregatePublicKeys(publicKeys, aggregatePublicKey, out var _);
            Console.WriteLine("Aggregate Public Key: [{0}] {1}", aggregatePublicKey.Length, HexMate.Convert.ToHexString(aggregatePublicKey));

            // Aggregate signatures
            var signatures = new Span<byte>(new byte[96 * 2]);
            signature1.CopyTo(signatures);
            signature2.CopyTo(signatures.Slice(96));
            using var blsAggregate = new BLSHerumi(new BLSParameters());
            var aggregateSignature = new byte[96];
            blsAggregate.TryAggregateSignatures(signatures, aggregateSignature, out var _);
            Console.WriteLine("Aggregate Signature: [{0}] {1}", aggregateSignature.Length, HexMate.Convert.ToHexString(aggregateSignature));

            // Verify aggregates
            // i.e. the combined aggregatePublicKey / aggregateSignature are a valid pair
            var aggregatePublicKeyParameters = new BLSParameters()
            {
                PublicKey = aggregatePublicKey
            };
            using var blsVerify = new BLSHerumi(aggregatePublicKeyParameters);
            var verifySuccess = blsVerify.VerifyHash(sharedMessageHash, aggregateSignature, domain1);
            Console.WriteLine("Verify: {0}", verifySuccess);

            verifySuccess.ShouldBeTrue();
        }


    }
}