// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
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

            byte[] sharedSecret1 = SecP256k1.EcdhSerialized(privateKey2.PublicKey.Bytes, privateKey1.KeyBytes);
            byte[] sharedSecret2 = SecP256k1.EcdhSerialized(privateKey1.PublicKey.Bytes, privateKey2.KeyBytes);

            Assert.AreEqual(sharedSecret1, sharedSecret2);
        }
    }
}
