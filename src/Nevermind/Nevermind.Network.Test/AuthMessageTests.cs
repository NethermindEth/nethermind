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

using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Core.Extensions;
using Nevermind.Core.Potocol;
using NUnit.Framework;
using Random = System.Random;

namespace Nevermind.Network.Test
{
    [TestFixture]
    public class AuthMessageTests
    {
        private const string TestPrivateKeyHex = "0x3a1076bf45ab87712ad64ccb3b10217737f7faacbf2872e88fdd9a537d8fe266";

        private readonly Random _random = new Random(1);

        private readonly PrivateKey _privateKey = new PrivateKey(TestPrivateKeyHex);

        private void TestEncodeDecode(Signer signer)
        {
            AuthMessage authMessage = new AuthMessage();
            authMessage.EphemeralPublicHash = new byte[AuthMessage.EphemeralHashLength];
            authMessage.Nonce = new byte[AuthMessage.NonceLength];
            authMessage.Signature = signer.Sign(_privateKey, Keccak.Compute("anything"));
            _random.NextBytes(authMessage.EphemeralPublicHash);
            authMessage.PublicKey = _privateKey.PublicKey;
            _random.NextBytes(authMessage.Nonce);
            authMessage.IsTokenUsed = true;
            byte[] data = AuthMessage.Encode(authMessage);
            AuthMessage after = AuthMessage.Decode(data);

            Assert.AreEqual(authMessage.Signature, after.Signature);
            Assert.True(Bytes.UnsafeCompare(authMessage.EphemeralPublicHash, after.EphemeralPublicHash));
            Assert.AreEqual(authMessage.PublicKey, after.PublicKey);
            Assert.True(Bytes.UnsafeCompare(authMessage.Nonce, after.Nonce));
            Assert.AreEqual(authMessage.IsTokenUsed, after.IsTokenUsed);
        }

        [TestCase(ChainId.MainNet)]
        [TestCase(ChainId.Morden)]
        [TestCase(ChainId.RootstockMainnet)]
        [TestCase(ChainId.DefaultGethPrivateChain)]
        [TestCase(ChainId.EthereumClassicMainnet)]
        [TestCase(ChainId.EthereumClassicTestnet)]
        public void Encode_decode_before_eip155(ChainId chainId)
        {
            Signer signer = new Signer(Frontier.Instance, chainId);
            TestEncodeDecode(signer);
        }

        [TestCase(ChainId.MainNet)]
        [TestCase(ChainId.Ropsten)]
        [TestCase(ChainId.Kovan)]
        public void Encode_decode_with_eip155(ChainId chainId)
        {
            Signer signer = new Signer(Byzantium.Instance, chainId);
            TestEncodeDecode(signer);
        }
    }
}