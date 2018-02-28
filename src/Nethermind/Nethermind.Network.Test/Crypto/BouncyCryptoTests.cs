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

using Nethermind.Core.Crypto;
using Nethermind.Network.Crypto;
using NUnit.Framework;

namespace Nethermind.Network.Test.Crypto
{
    [TestFixture]
    public class BouncyCryptoTests
    {
        [Test]
        public void Can_calculate_agreement()
        {
            CryptoRandom random = new CryptoRandom();
            PrivateKey privateKey1 = new PrivateKey(random.GenerateRandomBytes(32));
            PrivateKey privateKey2 = new PrivateKey(random.GenerateRandomBytes(32));

            byte[] sharedSecret1 = BouncyCrypto.Agree(privateKey1, privateKey2.PublicKey);
            byte[] sharedSecret2 = BouncyCrypto.Agree(privateKey2, privateKey1.PublicKey);

            Assert.AreEqual(sharedSecret1, sharedSecret2);
        }
    }
}