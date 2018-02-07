using Nevermind.Core.Extensions;
using NUnit.Framework;
using Org.BouncyCastle.Crypto.Digests;

namespace Nevermind.Network.Test
{
    [TestFixture]
    public class FramingServiceTests
    {
        private static EncryptionSecrets BuildSecrets()
        {
            EncryptionSecrets secrets = new EncryptionSecrets();
            secrets.AesSecret = NetTestVectors.AesSecret;
            secrets.MacSecret = NetTestVectors.MacSecret;

            byte[] bytes = NetTestVectors.AesSecret.Xor(NetTestVectors.MacSecret);

            KeccakDigest egressMac = new KeccakDigest(256);
            egressMac.BlockUpdate(bytes, 0, 32);
            secrets.EgressMac = egressMac;

            KeccakDigest ingressMac = new KeccakDigest(256);
            ingressMac.BlockUpdate(bytes, 0, 32);
            secrets.IngressMac = ingressMac;
            return secrets;
        }

        [Test]
        public void Size_looks_ok()
        {
            EncryptionSecrets secrets = BuildSecrets();

            FrameCodec framingService = new FrameCodec(secrets);
            byte[] packet = framingService.Write(0, 1, 0, new byte[1]);
            Assert.AreEqual(16 + 16 + 1 + 16 + 16, packet.Length);
        }

        [Test]
        public void Size_looks_ok_multiple_frames()
        {
            EncryptionSecrets secrets = BuildSecrets();
            FrameCodec framingService = new FrameCodec(secrets);
            byte[] packet = framingService.Write(0, 1, 0, new byte[FrameCodec.MaxFrameSize + 1]);
            Assert.AreEqual(16 + 16 + 1 /* packet type */ + 1024 + 16 /* frame boundary */ + 16 + 16 + 16 /* padded */ + 16, packet.Length);
        }
    }
}