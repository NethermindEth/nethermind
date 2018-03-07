/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using NUnit.Framework;

namespace Nethermind.Secp256k1.Test
{
    // TODO: test the output values
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
    }
}