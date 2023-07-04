// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

        private readonly Random _random = new(1);

        private readonly PrivateKey _privateKey = new(TestPrivateKeyHex);

        private readonly AckEip8MessageSerializer _serializer = new(new Eip8MessagePad(new CryptoRandom()));

        private void TestEncodeDecode()
        {
            AckEip8Message before = new();
            before.EphemeralPublicKey = _privateKey.PublicKey;
            before.Nonce = new byte[AckEip8MessageSerializer.NonceLength];
            _random.NextBytes(before.Nonce);
            byte[] data = _serializer.Serialize(before);
            AckEip8Message after = _serializer.Deserialize(data);

            Assert.That(after.EphemeralPublicKey, Is.EqualTo(before.EphemeralPublicKey));
            Assert.True(Bytes.AreEqual(before.Nonce, after.Nonce));
            Assert.That(after.Version, Is.EqualTo(0x04));
        }

        [Test]
        public void Test()
        {
            TestEncodeDecode();
        }
    }
}
