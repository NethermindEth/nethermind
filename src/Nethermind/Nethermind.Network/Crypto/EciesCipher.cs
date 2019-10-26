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
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;
using Nethermind.Secp256k1;
using Org.BouncyCastle.Crypto;
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
        private PrivateKeyGenerator _keyGenerator;

        public EciesCipher(ICryptoRandom cryptoRandom)
        {
            _cryptoRandom = cryptoRandom;
            _keyGenerator = new PrivateKeyGenerator(cryptoRandom);
        }

        public (bool, byte[]) Decrypt(PrivateKey privateKey, byte[] ciphertextBody, byte[] macData = null)
        {
            MemoryStream inputStream = new MemoryStream(ciphertextBody);
            int ephemBytesLength = 2 * ((BouncyCrypto.DomainParameters.Curve.FieldSize + 7) / 8) + 1;

            byte[] ephemBytes = new byte[ephemBytesLength];
            inputStream.Read(ephemBytes, 0, ephemBytesLength);
            byte[] iv = new byte[KeySize / 8];
            inputStream.Read(iv, 0, iv.Length);
            byte[] cipherBody = new byte[inputStream.Length - inputStream.Position];
            inputStream.Read(cipherBody, 0, cipherBody.Length);
            if (ephemBytes[0] != 4) // if not a compressed public key then probably we need to use EIP8
            {
                return (false, null);
            }

            byte[] plaintext = Decrypt(new PublicKey(ephemBytes), privateKey, iv, cipherBody, macData);
            return (true, plaintext);
        }

        public byte[] Encrypt(PublicKey recipientPublicKey, byte[] plaintext, byte[] macData)
        {
            byte[] iv = _cryptoRandom.GenerateRandomBytes(KeySize / 8);
            PrivateKey ephemeralPrivateKey = _keyGenerator.Generate();

            IIesEngine iesEngine = MakeIesEngine(true, recipientPublicKey, ephemeralPrivateKey, iv);

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
        
        private OptimizedKdf _optimizedKdf = new OptimizedKdf();

        private byte[] Decrypt(PublicKey ephemeralPublicKey, PrivateKey privateKey, byte[] iv, byte[] ciphertextBody, byte[] macData)
        {
            IIesEngine iesEngine = MakeIesEngine(false, ephemeralPublicKey, privateKey, iv);
            return iesEngine.ProcessBlock(ciphertextBody, 0, ciphertextBody.Length, macData);
        }

        private static IesParameters _iesParameters = new IesWithCipherParameters(new byte[] { }, new byte[] { }, KeySize, KeySize);
        
        private IIesEngine MakeIesEngine(bool isEncrypt, PublicKey publicKey, PrivateKey privateKey, byte[] iv)
        {
            AesEngine aesFastEngine = new AesEngine();

            EthereumIesEngine iesEngine = new EthereumIesEngine(
                new HMac(new Sha256Digest()),
                new Sha256Digest(),
                new BufferedBlockCipher(new SicBlockCipher(aesFastEngine)));

            
            ParametersWithIV parametersWithIV = new ParametersWithIV(_iesParameters, iv);
            byte[] secret = Proxy.EcdhSerialized(publicKey.Bytes, privateKey.KeyBytes);
            iesEngine.Init(isEncrypt, _optimizedKdf.Derive(secret), parametersWithIV);
            return iesEngine;
        }
    }
}