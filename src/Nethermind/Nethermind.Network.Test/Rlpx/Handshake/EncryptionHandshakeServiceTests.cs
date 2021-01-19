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

using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Specs;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx.Handshake
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class EncryptionHandshakeServiceTests
    {
        [SetUp]
        public void SetUp()
        {
            _trueCryptoRandom = new CryptoRandom();
            
            _testRandom = new TestRandom();

            _messageSerializationService = new MessageSerializationService();
            _messageSerializationService.Register(new AuthMessageSerializer());
            _messageSerializationService.Register(new AuthEip8MessageSerializer(new Eip8MessagePad(_testRandom)));
            _messageSerializationService.Register(new AckMessageSerializer());
            _messageSerializationService.Register(new AckEip8MessageSerializer(new Eip8MessagePad(_testRandom)));

            _eciesCipher = new EciesCipher(_trueCryptoRandom); // TODO: provide a separate test random with specific IV and epehemeral key for testing

            _initiatorService = new HandshakeService(_messageSerializationService, _eciesCipher, _testRandom, _ecdsa, NetTestVectors.StaticKeyA, LimboLogs.Instance);
            _recipientService = new HandshakeService(_messageSerializationService, _eciesCipher, _testRandom, _ecdsa, NetTestVectors.StaticKeyB, LimboLogs.Instance);

            _initiatorHandshake = new EncryptionHandshake();
            _recipientHandshake = new EncryptionHandshake();

            _auth = null;
            _ack = null;
        }

        private readonly IEthereumEcdsa _ecdsa = new EthereumEcdsa(ChainId.Ropsten, LimboLogs.Instance); // TODO: separate general crypto signer from Ethereum transaction signing

        private IMessageSerializationService _messageSerializationService;

        private TestRandom _testRandom;

        private ICryptoRandom _trueCryptoRandom;

        private IEciesCipher _eciesCipher;

        private IHandshakeService _initiatorService;

        private IHandshakeService _recipientService;

        private EncryptionHandshake _initiatorHandshake;
        private EncryptionHandshake _recipientHandshake;

        private Packet _auth;
        private Packet _ack;

        private void Auth(bool preEip8Format = false)
        {
            _auth = _initiatorService.Auth(NetTestVectors.StaticKeyB.PublicKey, _initiatorHandshake, preEip8Format);
        }

        private void Ack()
        {
            _ack = _recipientService.Ack(_recipientHandshake, _auth);
        }

        private void Agree()
        {
            _initiatorService.Agree(_initiatorHandshake, _ack);
        }
        
        private void InitializeRandom(bool preEip8Format = false)
        {
            // WARN: order reflects the internal implementation of the service (tests may fail after any refactoring)
            if (preEip8Format)
            {
                _testRandom.EnqueueRandomBytes(NetTestVectors.NonceA,
                    NetTestVectors.EphemeralKeyA.KeyBytes,
                    NetTestVectors.NonceB,
                    NetTestVectors.EphemeralKeyB.KeyBytes);                
            }
            else
            {
                _testRandom.EnqueueRandomBytes(NetTestVectors.NonceA,
                    NetTestVectors.EphemeralKeyA.KeyBytes,
                    _trueCryptoRandom.GenerateRandomBytes(100),
                    NetTestVectors.NonceB,
                    NetTestVectors.EphemeralKeyB.KeyBytes,
                    _trueCryptoRandom.GenerateRandomBytes(100));
            }
        }

        /// <summary>
        ///     https://github.com/ethereum/EIPs/blob/master/EIPS/eip-8.md
        /// </summary>
        [Test]
        public void Aes_and_mac_secrets_as_in_test_vectors()
        {
            InitializeRandom();
            Packet auth = _initiatorService.Auth(NetTestVectors.StaticKeyB.PublicKey, _initiatorHandshake);
            // TODO: cannot recover signature from this one...
            auth.Data = Bytes.FromHexString(
                "01b304ab7578555167be8154d5cc456f567d5ba302662433674222360f08d5f1534499d3678b513b" +
                "0fca474f3a514b18e75683032eb63fccb16c156dc6eb2c0b1593f0d84ac74f6e475f1b8d56116b84" +
                "9634a8c458705bf83a626ea0384d4d7341aae591fae42ce6bd5c850bfe0b999a694a49bbbaf3ef6c" +
                "da61110601d3b4c02ab6c30437257a6e0117792631a4b47c1d52fc0f8f89caadeb7d02770bf999cc" +
                "147d2df3b62e1ffb2c9d8c125a3984865356266bca11ce7d3a688663a51d82defaa8aad69da39ab6" +
                "d5470e81ec5f2a7a47fb865ff7cca21516f9299a07b1bc63ba56c7a1a892112841ca44b6e0034dee" +
                "70c9adabc15d76a54f443593fafdc3b27af8059703f88928e199cb122362a4b35f62386da7caad09" +
                "c001edaeb5f8a06d2b26fb6cb93c52a9fca51853b68193916982358fe1e5369e249875bb8d0d0ec3" +
                "6f917bc5e1eafd5896d46bd61ff23f1a863a8a8dcd54c7b109b771c8e61ec9c8908c733c0263440e" +
                "2aa067241aaa433f0bb053c7b31a838504b148f570c0ad62837129e547678c5190341e4f1693956c" +
                "3bf7678318e2d5b5340c9e488eefea198576344afbdf66db5f51204a6961a63ce072c8926c");

            Packet ack = _recipientService.Ack(_recipientHandshake, auth);
            ack.Data = Bytes.FromHexString(
                "01ea0451958701280a56482929d3b0757da8f7fbe5286784beead59d95089c217c9b917788989470" +
                "b0e330cc6e4fb383c0340ed85fab836ec9fb8a49672712aeabbdfd1e837c1ff4cace34311cd7f4de" +
                "05d59279e3524ab26ef753a0095637ac88f2b499b9914b5f64e143eae548a1066e14cd2f4bd7f814" +
                "c4652f11b254f8a2d0191e2f5546fae6055694aed14d906df79ad3b407d94692694e259191cde171" +
                "ad542fc588fa2b7333313d82a9f887332f1dfc36cea03f831cb9a23fea05b33deb999e85489e645f" +
                "6aab1872475d488d7bd6c7c120caf28dbfc5d6833888155ed69d34dbdc39c1f299be1057810f34fb" +
                "e754d021bfca14dc989753d61c413d261934e1a9c67ee060a25eefb54e81a4d14baff922180c395d" +
                "3f998d70f46f6b58306f969627ae364497e73fc27f6d17ae45a413d322cb8814276be6ddd13b885b" +
                "201b943213656cde498fa0e9ddc8e0b8f8a53824fbd82254f3e2c17e8eaea009c38b4aa0a3f306e8" +
                "797db43c25d68e86f262e564086f59a2fc60511c42abfb3057c247a8a8fe4fb3ccbadde17514b7ac" +
                "8000cdb6a912778426260c47f38919a91f25f4b5ffb455d6aaaf150f7e5529c100ce62d6d92826a7" +
                "1778d809bdf60232ae21ce8a437eca8223f45ac37f6487452ce626f549b3b5fdee26afd2072e4bc7" +
                "5833c2464c805246155289f4");

            _initiatorService.Agree(_initiatorHandshake, ack);
            
            Assert.AreEqual(NetTestVectors.AesSecret, _initiatorHandshake.Secrets.AesSecret, "initiator AES");
            Assert.AreEqual(NetTestVectors.AesSecret, _recipientHandshake.Secrets.AesSecret, "recipient AES");
            Assert.AreEqual(NetTestVectors.MacSecret, _initiatorHandshake.Secrets.MacSecret, "initiator MAC");
            Assert.AreEqual(NetTestVectors.MacSecret, _recipientHandshake.Secrets.MacSecret, "recipient MAC");

            // TODO: below failing, probably different format after serialization / during encryption (only tested decryption / deserialization in EciesCoder)
            // ingress uses the auth packet which is encrypted with a random IV and ephemeral key - need to remove that randomness for tests
            byte[] fooBytes = Encoding.ASCII.GetBytes("foo");
            _recipientHandshake.Secrets.IngressMac.BlockUpdate(fooBytes, 0, fooBytes.Length);

            byte[] ingressFooResult = new byte[32];
            _recipientHandshake.Secrets.IngressMac.DoFinal(ingressFooResult, 0);
            Assert.AreEqual(NetTestVectors.BIngressMacFoo, ingressFooResult, "recipient ingress foo");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Agrees_on_secrets(bool preEip8Format)
        {
            InitializeRandom(preEip8Format);
            Auth(preEip8Format);
            Ack();
            Agree();

//            Assert.AreEqual(_recipientHandshake.Secrets.Token, _initiatorHandshake.Secrets.Token, "Token");
            Assert.AreEqual(_recipientHandshake.Secrets.AesSecret, _initiatorHandshake.Secrets.AesSecret, "AES");
            Assert.AreEqual(_recipientHandshake.Secrets.MacSecret, _initiatorHandshake.Secrets.MacSecret, "MAC");

            byte[] recipientEgress = new byte[32];
            byte[] recipientIngress = new byte[32];

            byte[] initiatorEgress = new byte[32];
            byte[] initiatorIngress = new byte[32];

            _recipientHandshake.Secrets.EgressMac.DoFinal(recipientEgress, 0);
            _recipientHandshake.Secrets.IngressMac.DoFinal(recipientIngress, 0);

            _initiatorHandshake.Secrets.EgressMac.DoFinal(initiatorEgress, 0);
            _initiatorHandshake.Secrets.IngressMac.DoFinal(initiatorIngress, 0);

            Assert.AreEqual(initiatorEgress, recipientIngress, "Egress");
            Assert.AreEqual(initiatorIngress, recipientEgress, "Ingress");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Initiator_secrets_are_not_null(bool preEip8Format)
        {
            InitializeRandom(preEip8Format);
            Auth(preEip8Format);
            Ack();
            Agree();

//            Assert.NotNull(_recipientHandshake.Secrets.Token, "Token");
            Assert.NotNull(_initiatorHandshake.Secrets.AesSecret, "AES");
            Assert.NotNull(_initiatorHandshake.Secrets.MacSecret, "MAC");
            Assert.NotNull(_initiatorHandshake.Secrets.EgressMac, "Egress");
            Assert.NotNull(_initiatorHandshake.Secrets.IngressMac, "Ingress");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Recipient_secrets_are_not_null(bool preEip8Format)
        {
            InitializeRandom(preEip8Format);
            Auth(preEip8Format);
            Ack();
            Agree();

//            Assert.NotNull(_recipientHandshake.Secrets.Token, "Token");
            Assert.NotNull(_recipientHandshake.Secrets.AesSecret, "AES");
            Assert.NotNull(_recipientHandshake.Secrets.MacSecret, "MAC");
            Assert.NotNull(_recipientHandshake.Secrets.EgressMac, "Egress");
            Assert.NotNull(_recipientHandshake.Secrets.IngressMac, "Ingress");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Sets_ephemeral_key_on_ack(bool preEip8Format)
        {
            InitializeRandom(preEip8Format);
            Auth(preEip8Format);
            Ack();
            Assert.AreEqual(NetTestVectors.EphemeralKeyB, _recipientHandshake.EphemeralPrivateKey);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Sets_ephemeral_key_on_auth(bool preEip8Format)
        {
            InitializeRandom(preEip8Format);
            Auth(preEip8Format);
            Assert.AreEqual(NetTestVectors.EphemeralKeyA, _initiatorHandshake.EphemeralPrivateKey);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Sets_initiator_nonce_on_ack(bool preEip8Format)
        {
            InitializeRandom(preEip8Format);
            Auth(preEip8Format);
            Ack();
            Assert.AreEqual(NetTestVectors.NonceA, _recipientHandshake.InitiatorNonce);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Sets_initiator_nonce_on_auth(bool preEip8Format)
        {
            InitializeRandom(preEip8Format);
            Auth(preEip8Format);
            Assert.AreEqual(NetTestVectors.NonceA, _initiatorHandshake.InitiatorNonce);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Sets_recipient_nonce_on_ack(bool preEip8Format)
        {
            InitializeRandom(preEip8Format);
            Auth(preEip8Format);
            Ack();
            Assert.AreEqual(NetTestVectors.NonceB, _recipientHandshake.RecipientNonce);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Sets_recipient_nonce_on_agree(bool preEip8Format)
        {
            InitializeRandom(preEip8Format);
            Auth(preEip8Format);
            Ack();
            Agree();
            Assert.AreEqual(NetTestVectors.NonceB, _initiatorHandshake.RecipientNonce);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Sets_remote_ephemeral_key_on_ack(bool preEip8Format)
        {
            InitializeRandom(preEip8Format);
            Auth(preEip8Format);
            Ack();
            Assert.AreEqual(NetTestVectors.EphemeralKeyA.PublicKey, _recipientHandshake.RemoteEphemeralPublicKey);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Sets_remote_ephemeral_key_on_agree(bool preEip8Format)
        {
            InitializeRandom(preEip8Format);
            Auth(preEip8Format);
            Ack();
            Agree();
            Assert.AreEqual(NetTestVectors.EphemeralKeyB.PublicKey, _initiatorHandshake.RemoteEphemeralPublicKey);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Sets_remote_public_key_on_ack(bool preEip8Format)
        {
            InitializeRandom(preEip8Format);
            Auth(preEip8Format);
            Ack();
            Assert.AreEqual(NetTestVectors.StaticKeyA.PublicKey, _recipientHandshake.RemoteNodeId);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Sets_remote_public_key_on_auth(bool preEip8Format)
        {
            InitializeRandom(preEip8Format);
            Auth(preEip8Format);
            Assert.AreEqual(NetTestVectors.StaticKeyB.PublicKey, _initiatorHandshake.RemoteNodeId);
        }
    }
}
