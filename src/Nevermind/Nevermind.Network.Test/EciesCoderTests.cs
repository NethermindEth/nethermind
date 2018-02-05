using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Core.Extensions;
using NUnit.Framework;
using Org.BouncyCastle.Crypto.Parameters;

namespace Nevermind.Network.Test
{
    [TestFixture]
    public class EciesCoderTests
    {
        [Test]
        public void Can_decrypt_auth_message()
        {
            MessageSerializationService serializationService = new MessageSerializationService();
            serializationService.Register(new AuthMessageSerializer());

            Hex hex = new Hex("048ca79ad18e4b0659fab4853fe5bc58eb83992980f4c9cc147d2aa31532efd29a3d3dc6a3d89eaf" +
                              "913150cfc777ce0ce4af2758bf4810235f6e6ceccfee1acc6b22c005e9e3a49d6448610a58e98744" +
                              "ba3ac0399e82692d67c1f58849050b3024e21a52c9d3b01d871ff5f210817912773e610443a9ef14" +
                              "2e91cdba0bd77b5fdf0769b05671fc35f83d83e4d3b0b000c6b2a1b1bba89e0fc51bf4e460df3105" +
                              "c444f14be226458940d6061c296350937ffd5e3acaceeaaefd3c6f74be8e23e0f45163cc7ebd7622" +
                              "0f0128410fd05250273156d548a414444ae2f7dea4dfca2d43c057adb701a715bf59f6fb66b2d1d2" +
                              "0f2c703f851cbf5ac47396d9ca65b6260bd141ac4d53e2de585a73d1750780db4c9ee4cd4d225173" +
                              "a4592ee77e2bd94d0be3691f3b406f9bba9b591fc63facc016bfa8");


            ECPrivateKeyParameters privateParameters = BouncyCrypto.WrapPrivateKey(NetTestVectors.StaticKeyB.Hex);

            ICryptoRandom cryptoRandom = new CryptoRandom();
            EciesCoder coder = new EciesCoder(cryptoRandom);
            byte[] deciphered = coder.Decrypt(privateParameters, hex);

            AuthMessage authMessage = serializationService.Deserialize<AuthMessage>(deciphered);
            Assert.AreEqual(authMessage.PublicKey, NetTestVectors.StaticKeyA.PublicKey);
            Assert.AreEqual(authMessage.EphemeralPublicHash, Keccak.Compute(NetTestVectors.EphemeralKeyA.PublicKey.Bytes));
            Assert.AreEqual(authMessage.Nonce, NetTestVectors.NonceA);
            Assert.AreEqual(authMessage.IsTokenUsed, false);
            Assert.NotNull(authMessage.Signature);
        }

        [Test]
        public void Can_do_roundtrip()
        {
            ICryptoRandom cryptoRandom = new CryptoRandom();
            EciesCoder coder = new EciesCoder(cryptoRandom);

            (var privateKey, var publicKey) = BouncyCrypto.GenerateKeyPair();

            byte[] plainText = {1, 2, 3, 4, 5};
            byte[] cipherText = coder.Encrypt(publicKey.Q, plainText, null); // public(65) | IV(16) | cipher(...)

            byte[] ephemeralPublicKeyBytes = cipherText.Slice(0, 65);
            ECPublicKeyParameters ephemeralPublicKey = BouncyCrypto.WrapPublicKey(ephemeralPublicKeyBytes);
            byte[] iv = cipherText.Slice(65, 16);

            byte[] deciphered = coder.Decrypt(ephemeralPublicKey.Q, privateKey, iv, cipherText.Slice(81), null);
            Assert.AreEqual(plainText, deciphered);
        }
    }
}