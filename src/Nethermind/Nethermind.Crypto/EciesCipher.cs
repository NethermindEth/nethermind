//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Secp256k1;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace Nethermind.Crypto
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

        private static readonly int ephemBytesLength = 2 * ((BouncyCrypto.DomainParameters.Curve.FieldSize + 7) / 8) + 1;

        private static int allocSaved;

        public (bool, byte[]) Decrypt(PrivateKey privateKey, byte[] cipherText, byte[]? macData = null)
        {
            if (cipherText[0] != 4) // if not a compressed public key then probably we need to use EIP8
            {
                return (false, null);
            }
            
            Span<byte> ephemBytes = cipherText.AsSpan().Slice(0, ephemBytesLength);
            byte[] iv = cipherText.Slice(ephemBytesLength, KeySize / 8);
            byte[] cipherBody = cipherText.Slice(ephemBytesLength + KeySize / 8);

            byte[] plaintext = Decrypt(new PublicKey(ephemBytes), privateKey, iv, cipherBody, macData);
            return (true, plaintext);
        }

        public byte[] Encrypt(PublicKey recipientPublicKey, byte[] plainText, byte[] macData)
        {
            byte[] iv = _cryptoRandom.GenerateRandomBytes(KeySize / 8);
            PrivateKey ephemeralPrivateKey = _keyGenerator.Generate();
            IIesEngine iesEngine = MakeIesEngine(true, recipientPublicKey, ephemeralPrivateKey, iv);
            byte[] cipher = iesEngine.ProcessBlock(plainText, 0, plainText.Length, macData);
            
            using MemoryStream memoryStream = new();
            memoryStream.Write(ephemeralPrivateKey.PublicKey.PrefixedBytes, 0, ephemeralPrivateKey.PublicKey.PrefixedBytes.Length);
            memoryStream.Write(iv, 0, iv.Length);
            memoryStream.Write(cipher, 0, cipher.Length);
            return memoryStream.ToArray();
        }
        
        private OptimizedKdf _optimizedKdf = new();

        private byte[] Decrypt(PublicKey ephemeralPublicKey, PrivateKey privateKey, byte[] iv, byte[] ciphertextBody, byte[] macData)
        {
            IIesEngine iesEngine = MakeIesEngine(false, ephemeralPublicKey, privateKey, iv);
            return iesEngine.ProcessBlock(ciphertextBody, 0, ciphertextBody.Length, macData);
        }

        private static IesParameters _iesParameters = new IesWithCipherParameters(new byte[] { }, new byte[] { }, KeySize, KeySize);
        
        private IIesEngine MakeIesEngine(bool isEncrypt, PublicKey publicKey, PrivateKey privateKey, byte[] iv)
        {
            AesEngine aesFastEngine = new();

            EthereumIesEngine iesEngine = new(
                new HMac(new Sha256Digest()),
                new Sha256Digest(),
                new BufferedBlockCipher(new SicBlockCipher(aesFastEngine)));

            ParametersWithIV parametersWithIV = new(_iesParameters, iv);
            byte[] secret = Proxy.EcdhSerialized(publicKey.Bytes, privateKey.KeyBytes);
            iesEngine.Init(isEncrypt, _optimizedKdf.Derive(secret), parametersWithIV);
            return iesEngine;
        }
    }
}
