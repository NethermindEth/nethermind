// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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

        public (bool, byte[]) Decrypt(PrivateKey privateKey, byte[] cipherText, byte[]? macData = null)
        {
            if (cipherText[0] != 4) // if not a compressed public key then probably we need to use EIP8
            {
                return (false, null);
            }

            Span<byte> ephemBytes = cipherText.AsSpan(0, ephemBytesLength);
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

            byte[] prefixedBytes = ephemeralPrivateKey.PublicKey.PrefixedBytes;

            byte[] outputArray = new byte[prefixedBytes.Length + iv.Length + cipher.Length];
            Span<byte> outputSpan = outputArray;

            prefixedBytes.AsSpan().CopyTo(outputSpan);
            outputSpan = outputSpan[prefixedBytes.Length..];

            iv.AsSpan().CopyTo(outputSpan);
            outputSpan = outputSpan[iv.Length..];

            cipher.AsSpan().CopyTo(outputSpan);

            return outputArray;
        }

        private OptimizedKdf _optimizedKdf = new();

        private byte[] Decrypt(PublicKey ephemeralPublicKey, PrivateKey privateKey, byte[] iv, byte[] ciphertextBody, byte[] macData)
        {
            IIesEngine iesEngine = MakeIesEngine(false, ephemeralPublicKey, privateKey, iv);
            return iesEngine.ProcessBlock(ciphertextBody, 0, ciphertextBody.Length, macData);
        }

        private static IesParameters _iesParameters = new IesWithCipherParameters(Array.Empty<byte>(), Array.Empty<byte>(), KeySize, KeySize);

        private IIesEngine MakeIesEngine(bool isEncrypt, PublicKey publicKey, PrivateKey privateKey, byte[] iv)
        {
            IBlockCipher aesFastEngine = AesEngineX86Intrinsic.IsSupported ? new AesEngineX86Intrinsic() : new AesEngine();

            EthereumIesEngine iesEngine = new(
                new HMac(new Sha256Digest()),
                new Sha256Digest(),
                new BufferedBlockCipher(new SicBlockCipher(aesFastEngine)));

            ParametersWithIV parametersWithIV = new(_iesParameters, iv);
            byte[] secret = SecP256k1.EcdhSerialized(publicKey.Bytes, privateKey.KeyBytes);
            iesEngine.Init(isEncrypt, _optimizedKdf.Derive(secret), parametersWithIV);
            return iesEngine;
        }
    }
}
