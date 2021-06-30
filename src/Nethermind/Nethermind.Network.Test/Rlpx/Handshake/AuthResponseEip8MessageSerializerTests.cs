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

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Network.Rlpx.Handshake;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx.Handshake
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class AuthResponseEip8MessageSerializerTests
    {
        private const string TestPrivateKeyHex = "0x3a1076bf45ab87712ad64ccb3b10217737f7faacbf2872e88fdd9a537d8fe266";

        private readonly Random _random = new Random(1);

        private readonly PrivateKey _privateKey = new PrivateKey(TestPrivateKeyHex);

        private readonly AckEip8MessageSerializer _serializer = new AckEip8MessageSerializer(new Eip8MessagePad(new CryptoRandom()));

        private void TestEncodeDecode()
        {
            AckEip8Message before = new AckEip8Message();
            before.EphemeralPublicKey = _privateKey.PublicKey;
            before.Nonce = new byte[AckEip8MessageSerializer.NonceLength];
            _random.NextBytes(before.Nonce);
            byte[] data = _serializer.Serialize(before);
            AckEip8Message after = _serializer.Deserialize(data);

            Assert.AreEqual(before.EphemeralPublicKey, after.EphemeralPublicKey);
            Assert.True(Bytes.AreEqual(before.Nonce, after.Nonce));
            Assert.AreEqual(0x04, after.Version);
        }

        [Test]
        public void Test()
        {
            TestEncodeDecode();
        }
    }
}
