using System;
using System.Collections.Generic;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Core.Potocol;
using NUnit.Framework;

namespace Nevermind.Network.Test
{
    [TestFixture]
    public class EncryptionHandshakeServiceTests
    {
        [SetUp]
        public void SetUp()
        {
            _cryptoRandom = new TestRandom();
            _service = new EncryptionHandshakeService(_cryptoRandom, _signer);
            _remoteService = new EncryptionHandshakeService(_cryptoRandom, _signer);
        }

        private class TestRandom : ICryptoRandom
        {
            private readonly Queue<byte[]> _bytes = new Queue<byte[]>();

            public TestRandom()
            {
                _bytes.Enqueue(NetTestVectors.EphemeralKeyA.Hex);
                _bytes.Enqueue(NetTestVectors.NonceA);
                _bytes.Enqueue(NetTestVectors.EphemeralKeyB.Hex);
                _bytes.Enqueue(NetTestVectors.NonceB);
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

        private ICryptoRandom _cryptoRandom;

        private IEncryptionHandshakeService _service;

        private IEncryptionHandshakeService _remoteService;

        /// <summary>
        ///     https://github.com/ethereum/EIPs/blob/master/EIPS/eip-8.md
        /// </summary>
        [Test]
        public void Aes_and_mac_secrets_as_in_test_vectors()
        {
            EncryptionHandshake handshake = new EncryptionHandshake();
            handshake.RemotePublicKey = NetTestVectors.StaticKeyB.PublicKey;
            handshake.EphemeralPrivateKey = NetTestVectors.EphemeralKeyA;
            handshake.InitiatorNonce = NetTestVectors.NonceA;

            AuthResponseEip8Message responseMessage = new AuthResponseEip8Message();
            responseMessage.EphemeralPublicKey = NetTestVectors.EphemeralKeyB.PublicKey;
            responseMessage.Nonce = NetTestVectors.NonceB;

            _service.HandleResponse(handshake, responseMessage);
            Assert.AreEqual(NetTestVectors.AesSecret, handshake.Secrets.AesSecret, "AES");
            Assert.AreEqual(NetTestVectors.MacSecret, handshake.Secrets.MacSecret, "MAC");
        }

        [Test]
        public void Sets_ephemeral_key_on_init()
        {
            EncryptionHandshake handshake = new EncryptionHandshake();
            handshake.RemotePublicKey = NetTestVectors.StaticKeyB.PublicKey;
            _service.Init(handshake, NetTestVectors.StaticKeyA);
            Assert.AreEqual(NetTestVectors.EphemeralKeyA, handshake.EphemeralPrivateKey);
        }

        [Test]
        public void Sets_initiator_nonce_on_init()
        {
            EncryptionHandshake handshake = new EncryptionHandshake();
            handshake.RemotePublicKey = NetTestVectors.StaticKeyB.PublicKey;
            _service.Init(handshake, NetTestVectors.StaticKeyA);
            Assert.AreEqual(NetTestVectors.NonceA, handshake.InitiatorNonce);
        }

        [Test]
        public void Sets_initiator_nonce_on_respond()
        {
            EncryptionHandshake handshake = new EncryptionHandshake();
            handshake.RemotePublicKey = NetTestVectors.StaticKeyB.PublicKey;

            AuthEip8Message authMessage = _service.Init(handshake, NetTestVectors.StaticKeyA);
            _service.Respond(handshake, authMessage, NetTestVectors.StaticKeyB);

            Assert.AreEqual(NetTestVectors.NonceA, handshake.InitiatorNonce);
        }

        [Test]
        public void Sets_remote_ephemeral_key_on_handle_response()
        {
            EncryptionHandshake handshake = new EncryptionHandshake();
            handshake.EphemeralPrivateKey = NetTestVectors.EphemeralKeyA;
            handshake.InitiatorNonce = NetTestVectors.NonceA;

            AuthResponseEip8Message responseMessage = new AuthResponseEip8Message();
            responseMessage.EphemeralPublicKey = NetTestVectors.EphemeralKeyB.PublicKey;
            responseMessage.Nonce = NetTestVectors.NonceB;

            _service.HandleResponse(handshake, responseMessage);

            Assert.AreEqual(handshake.RemoteEphemeralPublicKey, NetTestVectors.EphemeralKeyB.PublicKey);
        }

        [Test]
        public void Sets_remote_ephemeral_key_on_respond()
        {
            EncryptionHandshake handshake = new EncryptionHandshake();
            handshake.RemotePublicKey = NetTestVectors.StaticKeyB.PublicKey;

            EncryptionHandshake remoteHandshake = new EncryptionHandshake();

            AuthEip8Message authMessage = _service.Init(handshake, NetTestVectors.StaticKeyA);
            _remoteService.Respond(remoteHandshake, authMessage, NetTestVectors.StaticKeyB);

            Signature sig = _signer.Sign(handshake.EphemeralPrivateKey, Keccak.Compute("asdadasasd"));
            PublicKey pub = _signer.RecoverPublicKey(sig, Keccak.Compute("asdadasasd"));

            Assert.AreEqual(handshake.EphemeralPrivateKey.PublicKey, pub, "debug");

            Assert.AreEqual(handshake.EphemeralPrivateKey, NetTestVectors.EphemeralKeyA, "Private");
            Assert.AreEqual(handshake.EphemeralPrivateKey.PublicKey, remoteHandshake.RemoteEphemeralPublicKey, "Public");
        }

        // TODO: need to decide how the handshake is initialized
        [Test]
        public void Sets_remote_public_key_on_init()
        {
            EncryptionHandshake handshake = new EncryptionHandshake();
            handshake.RemotePublicKey = NetTestVectors.StaticKeyB.PublicKey;

            _service.Init(handshake, NetTestVectors.StaticKeyA);

            Assert.AreEqual(handshake.RemotePublicKey, NetTestVectors.StaticKeyB.PublicKey);
        }

        [Test]
        public void Sets_remote_public_key_on_respond()
        {
            EncryptionHandshake handshake = new EncryptionHandshake();
            handshake.RemotePublicKey = NetTestVectors.StaticKeyB.PublicKey;

            EncryptionHandshake remoteHandshake = new EncryptionHandshake();

            AuthEip8Message authMessage = _service.Init(handshake, NetTestVectors.StaticKeyA);
            _remoteService.Respond(remoteHandshake, authMessage, NetTestVectors.StaticKeyB);

            Assert.AreEqual(remoteHandshake.RemotePublicKey, NetTestVectors.StaticKeyA.PublicKey);
        }

        [Test]
        public void Sets_responder_nonce()
        {
            EncryptionHandshake handshake = new EncryptionHandshake();
            handshake.RemotePublicKey = NetTestVectors.StaticKeyB.PublicKey;
            handshake.EphemeralPrivateKey = NetTestVectors.EphemeralKeyA;
            handshake.InitiatorNonce = NetTestVectors.NonceA;

            AuthResponseEip8Message responseMessage = new AuthResponseEip8Message();
            responseMessage.EphemeralPublicKey = NetTestVectors.EphemeralKeyB.PublicKey;
            responseMessage.Nonce = NetTestVectors.NonceB;

            _service.HandleResponse(handshake, responseMessage);

            Assert.AreEqual(handshake.ResponderNonce, NetTestVectors.NonceB);
        }

        [Test]
        public void Test_key_exchange()
        {
            EncryptionHandshake handshake = new EncryptionHandshake();
            handshake.RemotePublicKey = NetTestVectors.StaticKeyB.PublicKey;

            EncryptionHandshake remoteHandshake = new EncryptionHandshake();

            AuthEip8Message authMessage = _service.Init(handshake, NetTestVectors.StaticKeyA);
            AuthResponseEip8Message responseMessage = _remoteService.Respond(remoteHandshake, authMessage, NetTestVectors.StaticKeyB);
            _service.HandleResponse(handshake, responseMessage);

            Assert.AreEqual(handshake.Secrets.AesSecret, remoteHandshake.Secrets.AesSecret, "AES");
            Assert.AreEqual(handshake.Secrets.MacSecret, remoteHandshake.Secrets.MacSecret, "MAC");
            Assert.AreEqual(handshake.Secrets.EgressMac, remoteHandshake.Secrets.IngressMac, "Egress");
            Assert.AreEqual(handshake.Secrets.IngressMac, remoteHandshake.Secrets.EgressMac, "Ingress");

            Assert.NotNull(handshake.Secrets.AesSecret, "AES null");
            Assert.NotNull(handshake.Secrets.MacSecret, "MAC null");
            Assert.NotNull(handshake.Secrets.EgressMac, "Egress null");
            Assert.NotNull(handshake.Secrets.IngressMac, "Ingress null");
        }
    }
}