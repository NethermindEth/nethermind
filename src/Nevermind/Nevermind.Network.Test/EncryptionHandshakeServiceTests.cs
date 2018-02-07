using System;
using System.Collections.Generic;
using System.Text;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Core.Potocol;
using NUnit.Framework;
using Org.BouncyCastle.Crypto.Digests;

namespace Nevermind.Network.Test
{
    [TestFixture]
    public class EncryptionHandshakeServiceTests
    {
        [SetUp]
        public void SetUp()
        {
            _cryptoRandom = new TestRandom();
            _messageSerializationService = new MessageSerializationService();
            _messageSerializationService.Register(new AuthMessageSerializer());
            _messageSerializationService.Register(new AuthEip8MessageSerializer());
            _messageSerializationService.Register(new AckMessageSerializer());
            _messageSerializationService.Register(new AckEip8MessageSerializer());

            _eciesCipher = new EciesCipher(new CryptoRandom()); // TODO: provide a separate test random with specific IV and epehemeral key for testing

            _initiatorService = new EncryptionHandshakeService(_messageSerializationService, _eciesCipher, _cryptoRandom, _signer, NetTestVectors.StaticKeyA, NullLogger.Instance);
            _recipientService = new EncryptionHandshakeService(_messageSerializationService, _eciesCipher, _cryptoRandom, _signer, NetTestVectors.StaticKeyB, NullLogger.Instance);

            _initiatorHandshake = new EncryptionHandshake();
            _recipientHandshake = new EncryptionHandshake();

            _auth = null;
            _ack = null;
        }

        private class TestRandom : ICryptoRandom
        {
            private readonly Queue<byte[]> _bytes = new Queue<byte[]>();

            public TestRandom()
            {
                // WARN: order reflects the internal implementation of the service (tests may fail after any refactoring)
                _bytes.Enqueue(NetTestVectors.NonceA);
                _bytes.Enqueue(NetTestVectors.EphemeralKeyA.Hex);
                _bytes.Enqueue(NetTestVectors.NonceB);
                _bytes.Enqueue(NetTestVectors.EphemeralKeyB.Hex);
            }

            public byte[] GenerateRandomBytes(int length)
            {
                return new Hex(_bytes.Dequeue());
            }

            public int NextInt(int max)
            {
                throw new NotImplementedException();
            }
        }

        private readonly ISigner _signer = new Signer(Byzantium.Instance, ChainId.MainNet); // TODO: separate general crypto signer from Ethereum transaction signing

        private IMessageSerializationService _messageSerializationService;

        private ICryptoRandom _cryptoRandom;

        private IEciesCipher _eciesCipher;

        private IEncryptionHandshakeService _initiatorService;

        private IEncryptionHandshakeService _recipientService;

        private EncryptionHandshake _initiatorHandshake;
        private EncryptionHandshake _recipientHandshake;

        private Packet _auth;
        private Packet _ack;

        private void Auth()
        {
            _auth = _initiatorService.Auth(NetTestVectors.StaticKeyB.PublicKey, _initiatorHandshake);
        }

        private void Ack()
        {
            _ack = _recipientService.Ack(_recipientHandshake, _auth);
        }

        private void Agree()
        {
            _initiatorService.Agree(_initiatorHandshake, _ack);
        }

        /// <summary>
        ///     https://github.com/ethereum/EIPs/blob/master/EIPS/eip-8.md
        /// </summary>
        [Test]
        public void Aes_and_mac_secrets_as_in_test_vectors()
        {
            Packet auth = _initiatorService.Auth(NetTestVectors.StaticKeyB.PublicKey, _initiatorHandshake);
            Packet ack = _recipientService.Ack(_recipientHandshake, auth);
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
        
        [Test]
        public void Agrees_on_secrets()
        {
            Auth();
            Ack();
            Agree();

            Assert.AreEqual(_recipientHandshake.Secrets.Token, _initiatorHandshake.Secrets.Token, "Token");
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
            Assert.AreEqual(initiatorIngress, recipientIngress, "Ingress");
        }

        [Test]
        public void Initiator_secrets_are_not_null()
        {
            Auth();
            Ack();
            Agree();

            Assert.NotNull(_recipientHandshake.Secrets.Token, "Token");
            Assert.NotNull(_initiatorHandshake.Secrets.AesSecret, "AES");
            Assert.NotNull(_initiatorHandshake.Secrets.MacSecret, "MAC");
            Assert.NotNull(_initiatorHandshake.Secrets.EgressMac, "Egress");
            Assert.NotNull(_initiatorHandshake.Secrets.IngressMac, "Ingress");
        }

        [Test]
        public void Recipient_secrets_are_not_null()
        {
            Auth();
            Ack();
            Agree();

            Assert.NotNull(_recipientHandshake.Secrets.Token, "Token");
            Assert.NotNull(_recipientHandshake.Secrets.AesSecret, "AES");
            Assert.NotNull(_recipientHandshake.Secrets.MacSecret, "MAC");
            Assert.NotNull(_recipientHandshake.Secrets.EgressMac, "Egress");
            Assert.NotNull(_recipientHandshake.Secrets.IngressMac, "Ingress");
        }

        [Test]
        public void Sets_ephemeral_key_on_ack()
        {
            Auth();
            Ack();
            Assert.AreEqual(NetTestVectors.EphemeralKeyB, _recipientHandshake.EphemeralPrivateKey);
        }

        [Test]
        public void Sets_ephemeral_key_on_auth()
        {
            Auth();
            Assert.AreEqual(NetTestVectors.EphemeralKeyA, _initiatorHandshake.EphemeralPrivateKey);
        }

        [Test]
        public void Sets_initiator_nonce_on_ack()
        {
            Auth();
            Ack();
            Assert.AreEqual(NetTestVectors.NonceA, _recipientHandshake.InitiatorNonce);
        }

        [Test]
        public void Sets_initiator_nonce_on_auth()
        {
            Auth();
            Assert.AreEqual(NetTestVectors.NonceA, _initiatorHandshake.InitiatorNonce);
        }

        [Test]
        public void Sets_recipient_nonce_on_ack()
        {
            Auth();
            Ack();
            Assert.AreEqual(NetTestVectors.NonceB, _recipientHandshake.RecipientNonce);
        }

        [Test]
        public void Sets_recipient_nonce_on_agree()
        {
            Auth();
            Ack();
            Agree();
            Assert.AreEqual(NetTestVectors.NonceB, _initiatorHandshake.RecipientNonce);
        }

        [Test]
        public void Sets_remote_ephemeral_key_on_ack()
        {
            Auth();
            Ack();
            Assert.AreEqual(NetTestVectors.EphemeralKeyA.PublicKey, _recipientHandshake.RemoteEphemeralPublicKey);
        }

        [Test]
        public void Sets_remote_ephemeral_key_on_agree()
        {
            Auth();
            Ack();
            Agree();
            Assert.AreEqual(NetTestVectors.EphemeralKeyB.PublicKey, _initiatorHandshake.RemoteEphemeralPublicKey);
        }

        [Test]
        public void Sets_remote_public_key_on_ack()
        {
            Auth();
            Ack();
            Assert.AreEqual(NetTestVectors.StaticKeyA.PublicKey, _recipientHandshake.RemotePublicKey);
        }

        [Test]
        public void Sets_remote_public_key_on_auth()
        {
            Auth();
            Assert.AreEqual(NetTestVectors.StaticKeyB.PublicKey, _initiatorHandshake.RemotePublicKey);
        }
    }
}