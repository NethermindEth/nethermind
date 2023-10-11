// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Rlpx.Handshake;
using NUnit.Framework;
using Org.BouncyCastle.Utilities.Encoders;

namespace Nethermind.Network.Test.Rlpx.Handshake
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class AuthEip8MessageSerializerTests
    {
        private const string TestPrivateKeyHex = "0x3a1076bf45ab87712ad64ccb3b10217737f7faacbf2872e88fdd9a537d8fe266";

        private readonly Random _random = new(1);

        private readonly PrivateKey _privateKey = new(TestPrivateKeyHex);

        private readonly AuthEip8MessageSerializer _serializer = new(new Eip8MessagePad(new CryptoRandom()));

        private void TestEncodeDecode(IEthereumEcdsa ecdsa)
        {
            AuthEip8Message authMessage = new();
            authMessage.Nonce = new byte[AuthMessageSerializer.NonceLength]; // sic!
            authMessage.Signature = ecdsa.Sign(_privateKey, Keccak.Compute("anything"));
            authMessage.PublicKey = _privateKey.PublicKey;
            _random.NextBytes(authMessage.Nonce);
            byte[] data = _serializer.Serialize(authMessage);
            AuthEip8Message after = _serializer.Deserialize(data);

            Assert.That(after.Signature, Is.EqualTo(authMessage.Signature));
            Assert.That(after.PublicKey, Is.EqualTo(authMessage.PublicKey));
            Assert.True(Bytes.AreEqual(authMessage.Nonce, after.Nonce));
            Assert.That(after.Version, Is.EqualTo(authMessage.Version));
        }

        [TestCase(BlockchainIds.Mainnet)]
        [TestCase(BlockchainIds.Morden)]
        [TestCase(BlockchainIds.RootstockMainnet)]
        [TestCase(BlockchainIds.DefaultGethPrivateChain)]
        [TestCase(BlockchainIds.EthereumClassicMainnet)]
        [TestCase(BlockchainIds.EthereumClassicTestnet)]
        public void Encode_decode_before_eip155(int chainId)
        {
            EthereumEcdsa ecdsa = new(BlockchainIds.Olympic, LimboLogs.Instance);
            TestEncodeDecode(ecdsa);
        }

        [TestCase(BlockchainIds.Mainnet)]
        [TestCase(BlockchainIds.Sepolia)]
        [TestCase(BlockchainIds.Kovan)]
        public void Encode_decode_with_eip155(int chainId)
        {
            EthereumEcdsa ecdsa = new(BlockchainIds.Olympic, LimboLogs.Instance);
            TestEncodeDecode(ecdsa);
        }

        [Test]
        public void TestBadVersion()
        {
            string rawMsg = "f8bab841b054620e0d28697f4d4cbc1b25873d45ba17a62d724fea574982b76885ba164423b0160e39943610fe8c795f5d5167e2f3b5d52452d255a6dfb95d8339f0361f00b840a84e79d9c17895ec9720603c950293a5570727240e83774c128ea1654e64880ae2b1384167cab68dc3b2e7d1a741220a98c9842f36c5daa6094e586914516928a016e33b0bacaa8a7b493e2e43ec1bdb45b28f2f9111066228fb3c1063ffd8067d932b383f24302935273b29302a2f2b3d2621212a0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";
            IByteBuffer byteBuffer = Unpooled.Buffer();
            byteBuffer.WriteBytes(Hex.Decode(rawMsg));

            _serializer.Deserialize(byteBuffer);
        }
    }
}
