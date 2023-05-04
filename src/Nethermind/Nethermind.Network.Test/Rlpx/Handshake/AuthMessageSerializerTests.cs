// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Rlpx.Handshake;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx.Handshake
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class AuthMessageSerializerTests
    {
        private const string TestPrivateKeyHex = "0x3a1076bf45ab87712ad64ccb3b10217737f7faacbf2872e88fdd9a537d8fe266";

        private readonly Random _random = new(1);

        private readonly PrivateKey _privateKey = new(TestPrivateKeyHex);

        private readonly AuthMessageSerializer _serializer = new();

        private void TestEncodeDecode(IEthereumEcdsa ecdsa)
        {
            AuthMessage authMessage = new();
            authMessage.EphemeralPublicHash = new Keccak(new byte[AuthMessageSerializer.EphemeralHashLength]);
            authMessage.Nonce = new byte[AuthMessageSerializer.NonceLength];
            authMessage.Signature = ecdsa.Sign(_privateKey, Keccak.Compute("anything"));
            _random.NextBytes(authMessage.EphemeralPublicHash.Bytes);
            authMessage.PublicKey = _privateKey.PublicKey;
            _random.NextBytes(authMessage.Nonce);
            authMessage.IsTokenUsed = true;
            byte[] bytes = _serializer.Serialize(authMessage);
            AuthMessage after = _serializer.Deserialize(bytes);

            Assert.That(after.Signature, Is.EqualTo(authMessage.Signature));
            Assert.That(after.EphemeralPublicHash, Is.EqualTo(authMessage.EphemeralPublicHash));
            Assert.That(after.PublicKey, Is.EqualTo(authMessage.PublicKey));
            Assert.True(Bytes.AreEqual(authMessage.Nonce, after.Nonce));
            Assert.That(after.IsTokenUsed, Is.EqualTo(authMessage.IsTokenUsed));
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
        [TestCase(BlockchainIds.Ropsten)]
        [TestCase(BlockchainIds.Kovan)]
        public void Encode_decode_with_eip155(int chainId)
        {
            EthereumEcdsa ecdsa = new(BlockchainIds.Olympic, LimboLogs.Instance);
            TestEncodeDecode(ecdsa);
        }
    }
}
