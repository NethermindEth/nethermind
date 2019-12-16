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

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Secp256k1.Test
{
    [TestFixture]
    public class ProxyTests
    {
        [Test]
        public void Does_not_allow_empty_key()
        {
            byte[] privateKey = new byte[32];
            bool result =  Proxy.VerifyPrivateKey(privateKey);
            Assert.False(result);
        }
        
        /// <summary>
        /// https://en.bitcoin.it/wiki/Private_key
        /// Nearly every 256-bit number is a valid ECDSA private key. Specifically, any 256-bit number from 0x1 to 0xFFFF FFFF FFFF FFFF FFFF FFFF FFFF FFFE BAAE DCE6 AF48 A03B BFD2 5E8C D036 4140 is a valid private key.
        /// The range of valid private keys is governed by the secp256k1 ECDSA standard used by Bitcoin.
        /// </summary>
        [Test]
        public void Does_allow_valid_keys()
        {
            byte[] privateKey = new byte[32];
            privateKey[0] = 1;
            bool result =  Proxy.VerifyPrivateKey(privateKey);
            Assert.True(result);
        }
        
        [Test]
        public void Can_get_compressed_public_key()
        {
            byte[] privateKey = new byte[32];
            privateKey[0] = 1;
            byte[] publicKey =  Proxy.GetPublicKey(privateKey, true);
            Assert.AreEqual(33, publicKey.Length);
        }
        
        [Test]
        public void Can_get_uncompressed_public_key()
        {
            byte[] privateKey = new byte[32];
            privateKey[0] = 1;
            byte[] publicKey =  Proxy.GetPublicKey(privateKey, false);
            Assert.AreEqual(65, publicKey.Length);
        }
        
        [Test]
        public void Can_sign()
        {
            byte[] privateKey = new byte[32];
            privateKey[0] = 1;
            byte[] messageHash = new byte[32];
            messageHash[0] = 1;
            byte[] signature =  Proxy.SignCompact(messageHash, privateKey, out int recoveryId);
            Assert.AreEqual(64, signature.Length);
            Assert.AreEqual(1, recoveryId);
        }
        
        [Test]
        public void Can_recover_compressed()
        {
            byte[] privateKey = new byte[32];
            privateKey[0] = 1;
            byte[] messageHash = new byte[32];
            messageHash[0] = 1;
            byte[] signature =  Proxy.SignCompact(messageHash, privateKey, out int recoveryId);
            byte[] recovered =  Proxy.RecoverKeyFromCompact(messageHash, signature, recoveryId, true);
            Assert.AreEqual(33, recovered.Length);
        }
        
        [Test]
        public void Can_calculate_agreement()
        {
            byte[] privateKey = new byte[32];
            privateKey[0] = 1;
            byte[] publicKey = new byte[64];
            publicKey[0] = 1;
            byte[] result = new byte[32];
            Proxy.Ecdh(result, publicKey, privateKey);
        }
        
        [TestCase("103aaccf80ad53c11ce2d1654e733a70835b852bfa4528a6214f11a9b9c6e55c", "44007cacdca37c4fbdf1c22ea314e03a3e5b7d76e88fe02743af6c1f4786237d9b5a1e8e2781dde9d5caa3db193ab3c0364b6d5883216aa040b3c2e00a3f618f", "d0ab6bbdc1e1bc5c189d843a0ed4ae18bb76b1afbe4c2b6ffed66992402f8f90")]
        [TestCase("e9088ce6d8df1357233e1cde9ad58a910a26605bd1921570977d6708b96e37b5", "e41845daecae897d10025873e9ff98008819027d1503d8b04cdbdb987583852da0171ec64c04ab7234ee4a268124cade10bbb8db8c4dd49ca7da371ea4e3074b", "542c718db53e6b8af98f8903e2f6afa39da3b892d9bc9f152f87f8f3d9c046fb")]
        public void Can_calculate_agreement(string privateKeyStr, string publicKeyStr, string expectedSecretStr)
        {
            // Compute a shared key.
            byte[] result = new byte[32];
            byte[] publicKey = Bytes.FromHexString(publicKeyStr);
            byte[] privateKey = Bytes.FromHexString(privateKeyStr);
            Proxy.Ecdh(result, publicKey, privateKey);
            Assert.AreEqual(expectedSecretStr, result.ToHexString(false));
        }
        
        [TestCase("103aaccf80ad53c11ce2d1654e733a70835b852bfa4528a6214f11a9b9c6e55c", "7d2386471f6caf4327e08fe8767d5b3e3ae014a32ec2f1bd4f7ca3dcac7c00448f613f0ae0c2b340a06a2183586d4b36c0b33a19dba3cad5e9dd81278e1e5a9b", "d0ab6bbdc1e1bc5c189d843a0ed4ae18bb76b1afbe4c2b6ffed66992402f8f90")]
        public void Can_calculate_agreement_serialized(string privateKeyStr, string publicKeyStr, string expectedSecretStr)
        {
            // Compute a shared key.
            byte[] publicKey = Bytes.FromHexString(publicKeyStr);
            byte[] privateKey = Bytes.FromHexString(privateKeyStr);
            byte[] result = Proxy.EcdhSerialized(publicKey, privateKey);
            Assert.AreEqual(expectedSecretStr, result.ToHexString(false));
        }
        
        [Test]
        public void Can_recover_uncompressed()
        {
            byte[] privateKey = new byte[32];
            privateKey[0] = 1;
            byte[] messageHash = new byte[32];
            messageHash[0] = 1;
            byte[] signature =  Proxy.SignCompact(messageHash, privateKey, out int recoveryId);
            byte[] recovered =  Proxy.RecoverKeyFromCompact(messageHash, signature, recoveryId, false);
            Assert.AreEqual(65, recovered.Length);
        }
        
        [Test]
        public void can_recover_from_message()
        {
            var messageHex =
                "8F0120AB288C789ACF066672F9CBDDB551B921C8D1B2039361BA95970894BFBF5262062492F5A33D3EACE2929082574F1F0CED1A65FEA66D78FB7439DF2BA54B48F38B495EADCBA5F584D59F455467D376122C6A9B759AA3A973C1707BC67DA10001E004CB840AD062CF827668827668CB840AD062CF82766682766686016755793C86";
            var messageBytes = Bytes.FromHexString(messageHex);
            var mdc = messageBytes.Slice(0, 32);
            var signature = messageBytes.Slice(32, 65);
            var messageType = new[] { messageBytes[97] };
            var data = messageBytes.Slice(98, messageBytes.Length - 98);
            var signatureSlice = signature.Slice(0, 64);
            var recoveryId = signature[64];
            var signatureObject = new Signature(signatureSlice, recoveryId);
            var keccak = Keccak.Compute(Bytes.Concat(messageType, data));
            var publicKey = Proxy.RecoverKeyFromCompact(keccak.Bytes, signatureObject.Bytes, signatureObject.RecoveryId, false);
            Assert.AreEqual(65, publicKey.Length);
        }
    }
}