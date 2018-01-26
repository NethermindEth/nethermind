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
            _service = new EncryptionHandshakeService(_cryptoRandom, _signer);
            _remoteService = new EncryptionHandshakeService(_cryptoRandom, _signer);
        }

        private readonly ICryptoRandom _cryptoRandom = new CryptoRandom();

        private readonly ISigner _signer = new Signer(Byzantium.Instance, ChainId.MainNet); // TODO: separate general crypto signer from Ethereum transaction signing

        private IEncryptionHandshakeService _service;

        private IEncryptionHandshakeService _remoteService;

        [Test]
        public void Test_key_exchange()
        {
            EncryptionHandshake handshake = new EncryptionHandshake();
            EncryptionHandshake remoteHandshake = new EncryptionHandshake();

            AuthV4Message authMessage = _service.Init(handshake);
            AuthResponseV4Message responseMessage = _remoteService.Respond(remoteHandshake, authMessage);
            _service.HandleResponse(handshake, responseMessage);

            Assert.AreEqual(handshake.Secrets.EgressMac, remoteHandshake.Secrets.IngressMac);
            Assert.AreEqual(handshake.Secrets.IngressMac, remoteHandshake.Secrets.EgressMac);
        }
    }
}