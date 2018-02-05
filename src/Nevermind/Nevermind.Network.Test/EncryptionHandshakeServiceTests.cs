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
        private static readonly PrivateKey StaticKeyA = new PrivateKey("49a7b37aa6f6645917e7b807e9d1c00d4fa71f18343b0d4122a4d2df64dd6fee");
        private static readonly PrivateKey StaticKeyB = new PrivateKey("b71c71a67e1177ad4e901695e1b4b9ee17ae16c6668d313eac2f96dbcda3f291");
        private static readonly PrivateKey EphemeralKeyA = new PrivateKey("869d6ecf5211f1cc60418a13b9d870b22959d0c16f02bec714c960dd2298a32d");
        private static readonly PrivateKey EphemeralKeyB = new PrivateKey("e238eb8e04fee6511ab04c6dd3c89ce097b11f25d584863ac2b6d5b35b1847e4");
        private static readonly byte[] NonceA = new Hex("7e968bba13b6c50e2c4cd7f241cc0d64d1ac25c7f5952df231ac6a2bda8ee5d6");
        private static readonly byte[] NonceB = new Hex("559aead08264d5795d3909718cdd05abd49572e84fe55590eef31a88a08fdffd");
        
        private static readonly byte[] AesSecret = new Hex("80e8632c05fed6fc2a13b0f8d31a3cf645366239170ea067065aba8e28bac487");
        private static readonly byte[] MacSecret = new Hex("2ea74ec5dae199227dff1af715362700e989d889d7a493cb0639691efb8e5f98");
        
        [SetUp]
        public void SetUp()
        {
            _cryptoRandom = new TestRandom();
            _service = new EncryptionHandshakeService(_cryptoRandom, _signer);
            _remoteService = new EncryptionHandshakeService(_cryptoRandom, _signer);
        }

        private class TestRandom : ICryptoRandom
        {
            private readonly Queue<string> _bytes = new Queue<string>();

            public TestRandom()
            {
                _bytes.Enqueue("869d6ecf5211f1cc60418a13b9d870b22959d0c16f02bec714c960dd2298a32d");
                _bytes.Enqueue("7e968bba13b6c50e2c4cd7f241cc0d64d1ac25c7f5952df231ac6a2bda8ee5d6");
                _bytes.Enqueue("e238eb8e04fee6511ab04c6dd3c89ce097b11f25d584863ac2b6d5b35b1847e4");
                _bytes.Enqueue("559aead08264d5795d3909718cdd05abd49572e84fe55590eef31a88a08fdffd");
            }
            
            public byte[] GenerateRandomBytes(int length)
            {
                return new Hex(_bytes.Dequeue());
            }

            public int NextInt(int max)
            {
                throw new System.NotImplementedException();
            }
        }
        
        private readonly ISigner _signer = new Signer(Byzantium.Instance, ChainId.MainNet); // TODO: separate general crypto signer from Ethereum transaction signing

        private ICryptoRandom _cryptoRandom;
        
        private IEncryptionHandshakeService _service;

        private IEncryptionHandshakeService _remoteService;

        [Test]
        public void Test_key_exchange()
        {
            EncryptionHandshake handshake = new EncryptionHandshake();
            handshake.RemotePublicKey = StaticKeyB.PublicKey;
            
            EncryptionHandshake remoteHandshake = new EncryptionHandshake();

            AuthEip8Message authMessage = _service.Init(handshake, StaticKeyA);
            AuthResponseEip8Message responseMessage = _remoteService.Respond(remoteHandshake, authMessage, StaticKeyB);
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

        // TODO: need to decide how the handshake is initialized
        [Test]
        public void Sets_remote_public_key_on_init()
        {   
            EncryptionHandshake handshake = new EncryptionHandshake();
            handshake.RemotePublicKey = StaticKeyB.PublicKey;
            
            _service.Init(handshake, StaticKeyA);
            
            Assert.AreEqual(handshake.RemotePublicKey, StaticKeyB.PublicKey);
        }
        
        [Test]
        public void Sets_ephemeral_key_on_init()
        {   
            EncryptionHandshake handshake = new EncryptionHandshake();
            handshake.RemotePublicKey = StaticKeyB.PublicKey;
            _service.Init(handshake, StaticKeyA);
            Assert.AreEqual(EphemeralKeyA, handshake.EphemeralPrivateKey);
        }
        
        [Test]
        public void Sets_initiator_nonce_on_init()
        {   
            EncryptionHandshake handshake = new EncryptionHandshake();
            handshake.RemotePublicKey = StaticKeyB.PublicKey;
            _service.Init(handshake, StaticKeyA);
            Assert.AreEqual(NonceA, handshake.InitiatorNonce);
        }
        
        [Test]
        public void Sets_initiator_nonce_on_respond()
        {   
            EncryptionHandshake handshake = new EncryptionHandshake();
            handshake.RemotePublicKey = StaticKeyB.PublicKey;
            
            AuthEip8Message authMessage = _service.Init(handshake, StaticKeyA);
            _service.Respond(handshake, authMessage, StaticKeyB);
            
            Assert.AreEqual(NonceA, handshake.InitiatorNonce);
        }

        [Test]
        public void Sets_remote_ephemeral_key_on_respond()
        {   
            EncryptionHandshake handshake = new EncryptionHandshake();
            handshake.RemotePublicKey = StaticKeyB.PublicKey;
            
            EncryptionHandshake remoteHandshake = new EncryptionHandshake();
            
            AuthEip8Message authMessage = _service.Init(handshake, StaticKeyA);
            _remoteService.Respond(remoteHandshake, authMessage, StaticKeyB);

            Signature sig = _signer.Sign(handshake.EphemeralPrivateKey, Keccak.Compute("asdadasasd"));
            PublicKey pub = _signer.RecoverPublicKey(sig, Keccak.Compute("asdadasasd"));
            
            Assert.AreEqual(handshake.EphemeralPrivateKey.PublicKey, pub, "debug");
            
            Assert.AreEqual(handshake.EphemeralPrivateKey, EphemeralKeyA, "Private");
            Assert.AreEqual(handshake.EphemeralPrivateKey.PublicKey, remoteHandshake.RemoteEphemeralPublicKey, "Public");
        }
        
        [Test]
        public void Sets_remote_public_key_on_respond()
        {   
            EncryptionHandshake handshake = new EncryptionHandshake();
            handshake.RemotePublicKey = StaticKeyB.PublicKey;
            
            EncryptionHandshake remoteHandshake = new EncryptionHandshake();
            
            AuthEip8Message authMessage = _service.Init(handshake, StaticKeyA);
            _remoteService.Respond(remoteHandshake, authMessage, StaticKeyB);    
            
            Assert.AreEqual(remoteHandshake.RemotePublicKey, StaticKeyA.PublicKey);
        }
        
        [Test]
        public void Sets_remote_ephemeral_key_on_handle_response()
        {   
            EncryptionHandshake handshake = new EncryptionHandshake();
            handshake.EphemeralPrivateKey = EphemeralKeyA;
            handshake.InitiatorNonce = NonceA;
          
            AuthResponseEip8Message responseMessage = new AuthResponseEip8Message();
            responseMessage.EphemeralPublicKey = EphemeralKeyB.PublicKey;
            responseMessage.Nonce = NonceB;
                
            _service.HandleResponse(handshake, responseMessage);
            
            Assert.AreEqual(handshake.RemoteEphemeralPublicKey, EphemeralKeyB.PublicKey);
        }
        
        [Test]
        public void Sets_responder_nonce()
        {   
            EncryptionHandshake handshake = new EncryptionHandshake();
            handshake.RemotePublicKey = StaticKeyB.PublicKey;
            handshake.EphemeralPrivateKey = EphemeralKeyA;
            handshake.InitiatorNonce = NonceA;
          
            AuthResponseEip8Message responseMessage = new AuthResponseEip8Message();
            responseMessage.EphemeralPublicKey = EphemeralKeyB.PublicKey;
            responseMessage.Nonce = NonceB;
                
            _service.HandleResponse(handshake, responseMessage);
            
            Assert.AreEqual(handshake.ResponderNonce, NonceB);
        }
        
        /// <summary>
        /// https://github.com/ethereum/EIPs/blob/master/EIPS/eip-8.md
        /// </summary>
        [Test]
        public void Aes_and_mac_secrets_as_in_test_vectors()
        {   
            EncryptionHandshake handshake = new EncryptionHandshake();
            handshake.RemotePublicKey = StaticKeyB.PublicKey;
            handshake.EphemeralPrivateKey = EphemeralKeyA;
            handshake.InitiatorNonce = NonceA;
          
            AuthResponseEip8Message responseMessage = new AuthResponseEip8Message();
            responseMessage.EphemeralPublicKey = EphemeralKeyB.PublicKey;
            responseMessage.Nonce = NonceB;
                
            _service.HandleResponse(handshake, responseMessage);
            Assert.AreEqual(AesSecret, handshake.Secrets.AesSecret, "AES");
            Assert.AreEqual(MacSecret, handshake.Secrets.MacSecret, "MAC");
        }
    }
}