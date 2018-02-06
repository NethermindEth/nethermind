using System.IO;
using Nevermind.Core.Crypto;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Math.EC;
using Org.BouncyCastle.Security;

namespace Nevermind.Network
{
    /// <summary>
    ///     from EthereumJ
    /// </summary>
    public class EciesCipher : IEciesCipher
    {
        private const int KeySize = 128;
        private readonly ICryptoRandom _cryptoRandom;

        public EciesCipher(ICryptoRandom cryptoRandom)
        {
            _cryptoRandom = cryptoRandom;
        }

        public byte[] Decrypt(PrivateKey privateKey, byte[] ciphertextBody, byte[] macData = null)
        {
            MemoryStream inputStream = new MemoryStream(ciphertextBody);
            int ephemBytesLength = 2 * ((BouncyCrypto.DomainParameters.Curve.FieldSize + 7) / 8) + 1;

            byte[] ephemBytes = new byte[ephemBytesLength];
            inputStream.Read(ephemBytes, 0, ephemBytesLength);
            byte[] iv = new byte[KeySize / 8];
            inputStream.Read(iv, 0, iv.Length);
            byte[] cipherBody = new byte[inputStream.Length - inputStream.Position];
            inputStream.Read(cipherBody, 0, cipherBody.Length);

            byte[] plaintext = Decrypt(new PublicKey(ephemBytes), privateKey, iv, cipherBody, macData);

            return plaintext;
        }

        private byte[] Decrypt(PublicKey ephemeralPublicKey, PrivateKey privateKey, byte[] iv, byte[] ciphertextBody, byte[] macData)
        {
            AesFastEngine aesFastEngine = new AesFastEngine();

            EthereumIesEngine iesEngine = new EthereumIesEngine(
                new ECDHBasicAgreement(),
                new ConcatKdfBytesGenerator(new Sha256Digest()),
                new HMac(new Sha256Digest()),
                new Sha256Digest(),
                new BufferedBlockCipher(new SicBlockCipher(aesFastEngine)));


            byte[] d = { };
            byte[] e = { };

            IesParameters p = new IesWithCipherParameters(d, e, KeySize, KeySize);
            ParametersWithIV parametersWithIV =
                new ParametersWithIV(p, iv);

            ECPrivateKeyParameters privateKeyParameters = BouncyCrypto.WrapPrivateKey(privateKey);
            ECPublicKeyParameters publicKeyParameters = BouncyCrypto.WrapPublicKey(ephemeralPublicKey);
            iesEngine.Init(false, privateKeyParameters, publicKeyParameters, parametersWithIV);

            return iesEngine.ProcessBlock(ciphertextBody, 0, ciphertextBody.Length, macData);
        }

        public byte[] Encrypt(PublicKey recipientPublicKey, byte[] plaintext, byte[] macData)
        {
            ECKeyPairGenerator eGen = new ECKeyPairGenerator();
            SecureRandom random = new SecureRandom();
            KeyGenerationParameters gParam = new ECKeyGenerationParameters(BouncyCrypto.DomainParameters, random);

            eGen.Init(gParam);

            byte[] iv = _cryptoRandom.GenerateRandomBytes(KeySize / 8);

            AsymmetricCipherKeyPair ephemPair = eGen.GenerateKeyPair();
            ECPublicKeyParameters publicKeyParameters = BouncyCrypto.WrapPublicKey(recipientPublicKey);
            ECPrivateKeyParameters ephemeralPrivateKeyParameters = (ECPrivateKeyParameters)ephemPair.Private;
            ECPublicKeyParameters ephemeralPublicKeyParameters = (ECPublicKeyParameters)ephemPair.Public;
            EthereumIesEngine iesEngine = MakeIesEngine(true, publicKeyParameters.Q, ephemeralPrivateKeyParameters.D, iv);

            ECKeyGenerationParameters keygenParams = new ECKeyGenerationParameters(BouncyCrypto.DomainParameters, random);
            ECKeyPairGenerator generator = new ECKeyPairGenerator();
            generator.Init(keygenParams);

            ECKeyPairGenerator gen = new ECKeyPairGenerator();
            gen.Init(new ECKeyGenerationParameters(BouncyCrypto.DomainParameters, random));

            try
            {
                byte[] cipher = iesEngine.ProcessBlock(plaintext, 0, plaintext.Length, macData);
                MemoryStream bos = new MemoryStream();
                byte[] pubBytes = ephemeralPublicKeyParameters.Q.GetEncoded(false);
                bos.Write(pubBytes, 0, pubBytes.Length);
                bos.Write(iv, 0, iv.Length);
                bos.Write(cipher, 0, cipher.Length);
                return bos.ToArray();
            }
            catch (InvalidCipherTextException e)
            {
                throw;
            }
            catch (IOException e)
            {
                throw;
            }
        }

        private static EthereumIesEngine MakeIesEngine(bool isEncrypt, ECPoint pub, BigInteger prv, byte[] iv)
        {
            AesFastEngine aesFastEngine = new AesFastEngine();

            EthereumIesEngine iesEngine = new EthereumIesEngine(
                new ECDHBasicAgreement(),
                new ConcatKdfBytesGenerator(new Sha256Digest()),
                new HMac(new Sha256Digest()),
                new Sha256Digest(),
                new BufferedBlockCipher(new SicBlockCipher(aesFastEngine)));


            byte[] d = { };
            byte[] e = { };

            IesParameters p = new IesWithCipherParameters(d, e, KeySize, KeySize);
            ParametersWithIV parametersWithIV = new ParametersWithIV(p, iv);

            iesEngine.Init(isEncrypt, new ECPrivateKeyParameters(prv, BouncyCrypto.DomainParameters), new ECPublicKeyParameters(pub, BouncyCrypto.DomainParameters), parametersWithIV);
            return iesEngine;
        }
    }
}