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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Specs;
using Nethermind.Logging;
using Nethermind.Network.Rlpx.Handshake;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx.Handshake
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class AuthEip8MessageSerializerTests
    {
        private const string TestPrivateKeyHex = "0x3a1076bf45ab87712ad64ccb3b10217737f7faacbf2872e88fdd9a537d8fe266";

        private readonly Random _random = new Random(1);

        private readonly PrivateKey _privateKey = new PrivateKey(TestPrivateKeyHex);

        private readonly AuthEip8MessageSerializer _serializer = new AuthEip8MessageSerializer(new Eip8MessagePad(new CryptoRandom()));

        private void TestEncodeDecode(IEthereumEcdsa ecdsa)
        {
            AuthEip8Message authMessage = new AuthEip8Message();
            authMessage.Nonce = new byte[AuthMessageSerializer.NonceLength]; // sic!
            authMessage.Signature = ecdsa.Sign(_privateKey, Keccak.Compute("anything"));
            authMessage.PublicKey = _privateKey.PublicKey;
            _random.NextBytes(authMessage.Nonce);
            byte[] data = _serializer.Serialize(authMessage);
            AuthEip8Message after = _serializer.Deserialize(data);

            Assert.AreEqual(authMessage.Signature, after.Signature);
            Assert.AreEqual(authMessage.PublicKey, after.PublicKey);
            Assert.True(Bytes.AreEqual(authMessage.Nonce, after.Nonce));
            Assert.AreEqual(authMessage.Version, after.Version);
        }

        [TestCase(ChainId.Mainnet)]
        [TestCase(ChainId.Morden)]
        [TestCase(ChainId.RootstockMainnet)]
        [TestCase(ChainId.DefaultGethPrivateChain)]
        [TestCase(ChainId.EthereumClassicMainnet)]
        [TestCase(ChainId.EthereumClassicTestnet)]
        public void Encode_decode_before_eip155(int chainId)
        {
            EthereumEcdsa ecdsa = new EthereumEcdsa(ChainId.Olympic, LimboLogs.Instance);
            TestEncodeDecode(ecdsa);
        }

        [TestCase(ChainId.Mainnet)]
        [TestCase(ChainId.Ropsten)]
        [TestCase(ChainId.Kovan)]
        public void Encode_decode_with_eip155(int chainId)
        {
            EthereumEcdsa ecdsa = new EthereumEcdsa(ChainId.Olympic, LimboLogs.Instance);
            TestEncodeDecode(ecdsa);
        }
    }
}
