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

using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace Nethermind.Network.Crypto
{
    /// <summary>
    ///     Code adapted from ethereumJ (https://github.com/ethereum/ethereumj)
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

        public byte[] Encrypt(PublicKey recipientPublicKey, byte[] plaintext, byte[] macData)
        {
            byte[] iv = _cryptoRandom.GenerateRandomBytes(KeySize / 8);
            PrivateKey ephemeralPrivateKey = new PrivateKeyProvider(_cryptoRandom).PrivateKey;

            ECPublicKeyParameters publicKeyParameters = BouncyCrypto.WrapPublicKey(recipientPublicKey);
            ECPrivateKeyParameters ephemeralPrivateKeyParameters = BouncyCrypto.WrapPrivateKey(ephemeralPrivateKey);
            EthereumIesEngine iesEngine = MakeIesEngine(true, publicKeyParameters, ephemeralPrivateKeyParameters, iv);

            try
            {
                byte[] cipher = iesEngine.ProcessBlock(plaintext, 0, plaintext.Length, macData);
                MemoryStream memoryStream = new MemoryStream();
                memoryStream.Write(ephemeralPrivateKey.PublicKey.PrefixedBytes, 0, ephemeralPrivateKey.PublicKey.PrefixedBytes.Length);
                memoryStream.Write(iv, 0, iv.Length);
                memoryStream.Write(cipher, 0, cipher.Length);
                return memoryStream.ToArray();
            }
            catch (InvalidCipherTextException)
            {
                throw;
            }
            catch (IOException)
            {
                throw;
            }
        }

        private byte[] Decrypt(PublicKey ephemeralPublicKey, PrivateKey privateKey, byte[] iv, byte[] ciphertextBody, byte[] macData)
        {
            AesEngine aesFastEngine = new AesEngine();

            EthereumIesEngine iesEngine = new EthereumIesEngine(
                new ECDHBasicAgreement(),
                new ConcatKdfBytesGenerator(new Sha256Digest()),
                new HMac(new Sha256Digest()),
                new Sha256Digest(),
                new BufferedBlockCipher(new SicBlockCipher(aesFastEngine)));

            IesParameters iesParameters = new IesWithCipherParameters(new byte[] { }, new byte[] { }, KeySize, KeySize);
            ParametersWithIV parametersWithIV = new ParametersWithIV(iesParameters, iv);

            ECPrivateKeyParameters privateKeyParameters = BouncyCrypto.WrapPrivateKey(privateKey);
            ECPublicKeyParameters publicKeyParameters = BouncyCrypto.WrapPublicKey(ephemeralPublicKey);
            iesEngine.Init(false, privateKeyParameters, publicKeyParameters, parametersWithIV);

            return iesEngine.ProcessBlock(ciphertextBody, 0, ciphertextBody.Length, macData);
        }

        private static EthereumIesEngine MakeIesEngine(bool isEncrypt, ECPublicKeyParameters pub, ECPrivateKeyParameters prv, byte[] iv)
        {
            AesEngine aesFastEngine = new AesEngine();

            EthereumIesEngine iesEngine = new EthereumIesEngine(
                new ECDHBasicAgreement(),
                new ConcatKdfBytesGenerator(new Sha256Digest()),
                new HMac(new Sha256Digest()),
                new Sha256Digest(),
                new BufferedBlockCipher(new SicBlockCipher(aesFastEngine)));

            IesParameters iseParameters = new IesWithCipherParameters(new byte[] { }, new byte[] { }, KeySize, KeySize);
            ParametersWithIV parametersWithIV = new ParametersWithIV(iseParameters, iv);

            iesEngine.Init(isEncrypt, prv, pub, parametersWithIV);
            return iesEngine;
        }
    }
}