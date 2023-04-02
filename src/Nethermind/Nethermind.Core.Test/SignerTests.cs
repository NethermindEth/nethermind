// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class EcdsaTests
    {
        [OneTimeSetUp]
        public void SetUp()
        {
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
        }

        [TestCase("0x9242685bf161793cc25603c231bc2f568eb630ea16aa137d2664ac80388256084f8ae3bd7535248d0bd448298cc2e2071e56992d0774dc340c368ae950852ada1c")]
        [TestCase("0x34ff4b97a0ec8f735f781f250dcd3070a72ddb640072dd39553407d0320db79939e3b080ecaa2e9f248214c6f0811fb4b4ba05b7bcff254c053e47d8513e820900")]
        public void Hex_and_back_again(string hexSignature)
        {
            Signature signature = new(hexSignature);
            string hexAgain = signature.ToString();
            Assert.AreEqual(hexSignature, hexAgain);
        }

        [Test]
        public void Sign_and_recover()
        {
            EthereumEcdsa ethereumEcdsa = new(BlockchainIds.Olympic, LimboLogs.Instance);

            Keccak message = new Keccak(Keccak.Compute("Test message"));
            PrivateKey privateKey = Build.A.PrivateKey.TestObject;
            Signature signature = ethereumEcdsa.Sign(privateKey, message);
            Assert.AreEqual(privateKey.Address, ethereumEcdsa.RecoverAddress(signature, message));
        }

        [Test]
        public void Decompress()
        {
            EthereumEcdsa ethereumEcdsa = new(BlockchainIds.Olympic, LimboLogs.Instance);
            PrivateKey privateKey = Build.A.PrivateKey.TestObject;
            CompressedPublicKey compressedPublicKey = privateKey.CompressedPublicKey;
            PublicKey expected = privateKey.PublicKey;
            PublicKey actual = ethereumEcdsa.Decompress(compressedPublicKey);
            Assert.AreEqual(expected, actual);
        }
    }
}
