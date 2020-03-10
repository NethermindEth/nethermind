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
    public class RoundTripSignDataTest
    {
        private static IList<byte[]> MessageData => new List<byte[]>
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
        
        [TestMethod]
        public void DataSignAndVerify()
        {
            // Arrange
            var privateKey = HexMate.Convert.FromHexString(PrivateKeys[1]);
            var messageData = MessageData[1];

            Console.WriteLine("Input:");
            Console.WriteLine("Private Key: [{0}] {1}", privateKey.Length, HexMate.Convert.ToHexString(privateKey));
            Console.WriteLine("MessageData: [{0}] {1}", messageData.Length, HexMate.Convert.ToHexString(messageData));

            // Act
            var parameters = new BLSParameters()
            {
                PrivateKey = privateKey
            };
            using var bls = new BLSHerumi(parameters);

            var publicKey = new byte[48];
            _ = bls.TryExportBlsPublicKey(publicKey, out var _);

            Console.WriteLine("Public Key: [{0}] {1}", publicKey.Length, HexMate.Convert.ToHexString(publicKey));

            var signature = new byte[96];
            var signatureSuccess = bls.TrySignData(messageData, signature.AsSpan(), out var bytesWritten);

            Console.WriteLine("Signature: {0} [{1}] {2}", signatureSuccess, bytesWritten, HexMate.Convert.ToHexString(signature));

            var verifySuccess = bls.VerifyData(messageData, signature);
            Console.WriteLine("Verify1: {0}", verifySuccess);

            var parameters2 = new BLSParameters()
            {
                PublicKey = publicKey
            };
            using var bls2 = new BLSHerumi(parameters);

            var verifySuccess2 = bls2.VerifyData(messageData, signature);
            Console.WriteLine("Verify2: {0}", verifySuccess2);

            verifySuccess2.ShouldBeTrue();
        }

        [TestMethod]
        public void SignTwoDataAndAggregateVerify()
        {
            // Arrange
            var privateKey1 = HexMate.Convert.FromHexString(PrivateKeys[1]);
            var privateKey2 = HexMate.Convert.FromHexString(PrivateKeys[2]);
            var messageData1 = MessageData[1];
            var messageData2 = MessageData[2];
        
            Console.WriteLine("Input:");
            Console.WriteLine("Private Key 1: [{0}] {1}", privateKey1.Length, HexMate.Convert.ToHexString(privateKey1));
            Console.WriteLine("MessageData 1: [{0}] {1}", messageData1.Length, HexMate.Convert.ToHexString(messageData1));
            Console.WriteLine("Private Key 2: [{0}] {1}", privateKey2.Length, HexMate.Convert.ToHexString(privateKey2));
            Console.WriteLine("MessageData 2: [{0}] {1}", messageData2.Length, HexMate.Convert.ToHexString(messageData2));
        
            // Sign 1
            using var bls1 = new BLSHerumi(new BLSParameters() { PrivateKey = privateKey1 });
            var signature1 = new byte[96];
            _ = bls1.TrySignData(messageData1, signature1.AsSpan(), out var _);
            Console.WriteLine("Signature 1: [{0}] {1}", signature1.Length, HexMate.Convert.ToHexString(signature1));
            var publicKey1 = new byte[48];
            _ = bls1.TryExportBlsPublicKey(publicKey1, out var _);
            Console.WriteLine("Public Key 1: [{0}] {1}", publicKey1.Length, HexMate.Convert.ToHexString(publicKey1));
        
            // Sign 2
            using var bls2 = new BLSHerumi(new BLSParameters() { PrivateKey = privateKey2 });
            var signature2 = new byte[96];
            _ = bls2.TrySignData(messageData2, signature2.AsSpan(), out var _);
            Console.WriteLine("Signature 2: [{0}] {1}", signature2.Length, HexMate.Convert.ToHexString(signature2));
            var publicKey2 = new byte[48];
            _ = bls2.TryExportBlsPublicKey(publicKey2, out var _);
            Console.WriteLine("Public Key 2: [{0}] {1}", publicKey2.Length, HexMate.Convert.ToHexString(publicKey2));

            // Concatenate public keys
            var publicKeys = new Span<byte>(new byte[48 * 2]);
            publicKey1.CopyTo(publicKeys);
            publicKey2.CopyTo(publicKeys.Slice(48));

            // Aggregate signatures
            var signatures = new Span<byte>(new byte[96 * 2]);
            signature1.CopyTo(signatures);
            signature2.CopyTo(signatures.Slice(96));
            using var blsAggregate = new BLSHerumi(new BLSParameters());
            var aggregateSignature = new byte[96];
            blsAggregate.TryAggregateSignatures(signatures, aggregateSignature, out var _);
            Console.WriteLine("Aggregate Signature: [{0}] {1}", aggregateSignature.Length, HexMate.Convert.ToHexString(aggregateSignature));

            // Concatenate data
            var data = new Span<byte>(new byte[32 * 2]);
            messageData1.CopyTo(data);
            messageData2.CopyTo(data.Slice(32));

            // Aggregate verify
            using var blsVerify = new BLSHerumi(new BLSParameters());
            var verifySuccess = blsVerify.AggregateVerifyData(publicKeys, data, aggregateSignature);
            Console.WriteLine("Verify: {0}", verifySuccess);
        
            verifySuccess.ShouldBeTrue();
        }
        
        [TestMethod]
        public void SignSharedDataAndFastAggregateVerify()
        {
            // Arrange
            var privateKey1 = HexMate.Convert.FromHexString(PrivateKeys[1]);
            var privateKey2 = HexMate.Convert.FromHexString(PrivateKeys[2]);
            var sharedMessageData = MessageData[2];
        
            Console.WriteLine("Input:");
            Console.WriteLine("Private Key 1: [{0}] {1}", privateKey1.Length, HexMate.Convert.ToHexString(privateKey1));
            Console.WriteLine("Private Key 2: [{0}] {1}", privateKey2.Length, HexMate.Convert.ToHexString(privateKey2));
            Console.WriteLine("MessageData 2: [{0}] {1}", sharedMessageData.Length, HexMate.Convert.ToHexString(sharedMessageData));
        
            // Sign 1
            using var bls1 = new BLSHerumi(new BLSParameters() { PrivateKey = privateKey1 });
            var signature1 = new byte[96];
            _ = bls1.TrySignData(sharedMessageData, signature1.AsSpan(), out var _);
            Console.WriteLine("Signature 1: [{0}] {1}", signature1.Length, HexMate.Convert.ToHexString(signature1));
            var publicKey1 = new byte[48];
            _ = bls1.TryExportBlsPublicKey(publicKey1, out var _);
            Console.WriteLine("Public Key 1: [{0}] {1}", publicKey1.Length, HexMate.Convert.ToHexString(publicKey1));
        
            // Sign 2
            using var bls2 = new BLSHerumi(new BLSParameters() { PrivateKey = privateKey2 });
            var signature2 = new byte[96];
            _ = bls2.TrySignData(sharedMessageData, signature2.AsSpan(), out var _);
            Console.WriteLine("Signature 2: [{0}] {1}", signature2.Length, HexMate.Convert.ToHexString(signature2));
            var publicKey2 = new byte[48];
            _ = bls2.TryExportBlsPublicKey(publicKey2, out var _);
            Console.WriteLine("Public Key 2: [{0}] {1}", publicKey2.Length, HexMate.Convert.ToHexString(publicKey2));
        
            // Concatenate public keys
            var publicKeys = new Span<byte>(new byte[48 * 2]);
            publicKey1.CopyTo(publicKeys);
            publicKey2.CopyTo(publicKeys.Slice(48));
        
            // Aggregate signatures
            var signatures = new Span<byte>(new byte[96 * 2]);
            signature1.CopyTo(signatures);
            signature2.CopyTo(signatures.Slice(96));
            using var blsAggregate = new BLSHerumi(new BLSParameters());
            var aggregateSignature = new byte[96];
            blsAggregate.TryAggregateSignatures(signatures, aggregateSignature, out var _);
            Console.WriteLine("Aggregate Signature: [{0}] {1}", aggregateSignature.Length, HexMate.Convert.ToHexString(aggregateSignature));
        
            // Fast aggregate verify
            using var blsVerify = new BLSHerumi(new BLSParameters());
            var verifySuccess = blsVerify.FastAggregateVerifyData(publicKeys, sharedMessageData, aggregateSignature);
            Console.WriteLine("Verify: {0}", verifySuccess);
        
            verifySuccess.ShouldBeTrue();
        }
    }
}