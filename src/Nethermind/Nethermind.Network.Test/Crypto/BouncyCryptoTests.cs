//  Copyright (c) 2021 Demerzel Solutions Limited
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

using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Secp256k1;
using NUnit.Framework;

namespace Nethermind.Network.Test.Crypto
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class BouncyCryptoTests
    {
        [Test]
        public void Can_calculate_agreement()
        {
            PrivateKey privateKey1 = TestItem.PrivateKeyA;
            PrivateKey privateKey2 = TestItem.PrivateKeyB;

            byte[] sharedSecret1 = BouncyCrypto.Agree(privateKey1, privateKey2.PublicKey);
            byte[] sharedSecret2 = BouncyCrypto.Agree(privateKey2, privateKey1.PublicKey);

            Assert.AreEqual(sharedSecret1, sharedSecret2);
        }
        
        [Test]
        public void Can_calculate_agreement_proxy()
        {
            PrivateKey privateKey1 = TestItem.PrivateKeyA;
            PrivateKey privateKey2 = TestItem.PrivateKeyB;

            byte[] sharedSecret1 = Proxy.EcdhSerialized(privateKey2.PublicKey.Bytes, privateKey1.KeyBytes);
            byte[] sharedSecret2 = Proxy.EcdhSerialized(privateKey1.PublicKey.Bytes, privateKey2.KeyBytes);

            Assert.AreEqual(sharedSecret1, sharedSecret2);
        }
    }
}
