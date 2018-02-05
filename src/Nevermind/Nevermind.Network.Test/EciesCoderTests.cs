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
        public void Can_decrypt_auth_eip8_message()
        {
            MessageSerializationService serializationService = new MessageSerializationService();
            serializationService.Register(new AuthEip8MessageSerializer());

            Hex hex = new Hex("01b304ab7578555167be8154d5cc456f567d5ba302662433674222360f08d5f1534499d3678b513b" +
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

            ECPrivateKeyParameters privateParameters = BouncyCrypto.WrapPrivateKey(NetTestVectors.StaticKeyB.Hex);

            byte[] allBytes = hex;
            byte[] sizeBytes = allBytes.Slice(0, 2);
            int size = sizeBytes.ToInt32();

            ICryptoRandom cryptoRandom = new CryptoRandom();
            EciesCoder coder = new EciesCoder(cryptoRandom);
            byte[] deciphered = coder.Decrypt(privateParameters, allBytes.Slice(2, size), sizeBytes);

            AuthEip8Message authMessage = serializationService.Deserialize<AuthEip8Message>(deciphered);
            Assert.AreEqual(authMessage.PublicKey, NetTestVectors.StaticKeyA.PublicKey);
            Assert.AreEqual(authMessage.Nonce, NetTestVectors.NonceA);
            Assert.AreEqual(authMessage.Version, 4);
            Assert.NotNull(authMessage.Signature);
        }

        [Test]
        public void Can_decrypt_auth_eip8_message_with_additional_elements()
        {
            MessageSerializationService serializationService = new MessageSerializationService();
            serializationService.Register(new AuthEip8MessageSerializer());

            Hex hex = new Hex("01b8044c6c312173685d1edd268aa95e1d495474c6959bcdd10067ba4c9013df9e40ff45f5bfd6f7" +
                              "2471f93a91b493f8e00abc4b80f682973de715d77ba3a005a242eb859f9a211d93a347fa64b597bf" +
                              "280a6b88e26299cf263b01b8dfdb712278464fd1c25840b995e84d367d743f66c0e54a586725b7bb" +
                              "f12acca27170ae3283c1073adda4b6d79f27656993aefccf16e0d0409fe07db2dc398a1b7e8ee93b" +
                              "cd181485fd332f381d6a050fba4c7641a5112ac1b0b61168d20f01b479e19adf7fdbfa0905f63352" +
                              "bfc7e23cf3357657455119d879c78d3cf8c8c06375f3f7d4861aa02a122467e069acaf513025ff19" +
                              "6641f6d2810ce493f51bee9c966b15c5043505350392b57645385a18c78f14669cc4d960446c1757" +
                              "1b7c5d725021babbcd786957f3d17089c084907bda22c2b2675b4378b114c601d858802a55345a15" +
                              "116bc61da4193996187ed70d16730e9ae6b3bb8787ebcaea1871d850997ddc08b4f4ea668fbf3740" +
                              "7ac044b55be0908ecb94d4ed172ece66fd31bfdadf2b97a8bc690163ee11f5b575a4b44e36e2bfb2" +
                              "f0fce91676fd64c7773bac6a003f481fddd0bae0a1f31aa27504e2a533af4cef3b623f4791b2cca6" +
                              "d490");

            ECPrivateKeyParameters privateParameters = BouncyCrypto.WrapPrivateKey(NetTestVectors.StaticKeyB.Hex);

            byte[] allBytes = hex;
            byte[] sizeBytes = allBytes.Slice(0, 2);
            int size = sizeBytes.ToInt32();

            ICryptoRandom cryptoRandom = new CryptoRandom();
            EciesCoder coder = new EciesCoder(cryptoRandom);
            byte[] deciphered = coder.Decrypt(privateParameters, allBytes.Slice(2, size), sizeBytes);

            AuthEip8Message authMessage = serializationService.Deserialize<AuthEip8Message>(deciphered);
            Assert.AreEqual(authMessage.PublicKey, NetTestVectors.StaticKeyA.PublicKey);
            Assert.AreEqual(authMessage.Nonce, NetTestVectors.NonceA);
            Assert.AreEqual(authMessage.Version, 4);
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