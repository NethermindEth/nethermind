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
using Nevermind.Core.Encoding;
using Nevermind.Core.Extensions;
using Nevermind.Core.Potocol;
using NUnit.Framework;
using Random = System.Random;

namespace Nevermind.Network.Test
{
    [TestFixture]
    public class AuthMessageV4Tests
    {
        private const string TestPrivateKeyHex = "0x3a1076bf45ab87712ad64ccb3b10217737f7faacbf2872e88fdd9a537d8fe266";

        private readonly Random _random = new Random(1);

        private readonly PrivateKey _privateKey = new PrivateKey(TestPrivateKeyHex);

        private void TestEncodeDecode(Signer signer)
        {
            AuthMessageV4 authMessage = new AuthMessageV4();
            authMessage.Nonce = new byte[AuthMessage.NonceLength]; // sic!
            authMessage.Signature = signer.Sign(_privateKey, Keccak.Compute("anything"));
            authMessage.PublicKey = _privateKey.PublicKey;
            _random.NextBytes(authMessage.Nonce);
            byte[] data = AuthMessageV4.Encode(authMessage);
            AuthMessageV4 after = AuthMessageV4.Decode(data);

            Assert.AreEqual(authMessage.Signature, after.Signature);
            Assert.AreEqual(authMessage.PublicKey, after.PublicKey);
            Assert.True(Bytes.UnsafeCompare(authMessage.Nonce, after.Nonce));
            Assert.AreEqual(authMessage.Version, after.Version);
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

        [Test]
        public void Eip_8_auth_test_1()
        {
            string auth = "01b304ab7578555167be8154d5cc456f567d5ba302662433674222360f08d5f1534499d3678b513b" +
"0fca474f3a514b18e75683032eb63fccb16c156dc6eb2c0b1593f0d84ac74f6e475f1b8d56116b84" +
"9634a8c458705bf83a626ea0384d4d7341aae591fae42ce6bd5c850bfe0b999a694a49bbbaf3ef6c" +
"da61110601d3b4c02ab6c30437257a6e0117792631a4b47c1d52fc0f8f89caadeb7d02770bf999cc" +
"147d2df3b62e1ffb2c9d8c125a3984865356266bca11ce7d3a688663a51d82defaa8aad69da39ab6" +
"d5470e81ec5f2a7a47fb865ff7cca21516f9299a07b1bc63ba56c7a1a892112841ca44b6e0034dee" +
"70c9adabc15d76a54f443593fafdc3b27af8059703f88928e199cb122362a4b35f62386da7caad09" +
"c001edaeb5f8a06d2b26fb6cb93c52a9fca51853b68193916982358fe1e5369e249875bb8d0d0ec3" +
"6f917bc5e1eafd5896d46bd61ff23f1a863a8a8dcd54c7b109b771c8e61ec9c8908c733c0263440e" +
"2aa067241aaa433f0bb053c7b31a838504b148f570c0ad62837129e547678c5190341e4f1693956c" +
"3bf7678318e2d5b5340c9e488eefea198576344afbdf66db5f51204a6961a63ce072c8926c";

            string hello = "f87137916b6e6574682f76302e39312f706c616e39cdc5836574683dc6846d6f726b1682270fb840" +
                "fda1cff674c90c9a197539fe3dfb53086ace64f83ed7c6eabec741f7f381cc803e52ab2cd55d5569" +
                "bce4347107a310dfd5f88a010cd2ffd1005ca406f1842877c883666f6f836261720304";

            object helloRlp = Rlp.Decode(new Rlp(Hex.ToBytes(hello)));


            AuthMessageV4 message = AuthMessageV4.Decode(Hex.ToBytes(auth));
        }
    }
}