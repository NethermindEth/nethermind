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

using System;
using System.Diagnostics;
using Nethermind.Core.Extensions;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Utilities;

namespace Nethermind.Network.Crypto
{
    /// <summary>
    ///     Code adapted from ethereumJ (https://github.com/ethereum/ethereumj)
    ///     Support class for constructing integrated encryption cipher
    ///     for doing basic message exchanges on top of key agreement ciphers.
    ///     Follows the description given in IEEE Std 1363a with a couple of changes
    ///     specific to Ethereum:
    ///     -Hash the MAC key before use
    ///     -Include the encryption IV in the MAC computation
    /// </summary>
    public class EthereumIesEngine : IIesEngine
    {
        private byte[] _kdfKey;

        private readonly IDigest _hash;

        private bool _forEncryption;
        private IesParameters _iesParameters;
        private byte[] _iv;

        private byte[] V;

        /**
     * set up for use with stream mode, where the key derivation function
     * is used to provide a stream of bytes to xor with the message.
     *  @param agree the key agreement used as the basis for the encryption
     * @param kdf    the key derivation function used for byte generation
     * @param mac    the message authentication code generator for the message
     * @param hash   hash ing function
     * @param cipher the actual cipher
     */
        public EthereumIesEngine(
            IMac mac,
            IDigest hash,
            BufferedBlockCipher cipher)
        {
            Mac = mac;
            _hash = hash;
            Cipher = cipher;
        }

        public bool HashK2 { get; set; } = true;

        public IMac Mac { get; }
        public BufferedBlockCipher Cipher { get; }

        /**
     * Initialise the encryptor.
     *
     * @param forEncryption whether or not this is encryption/decryption.
     * @param privParam     our private key parameters
     * @param pubParam      the recipient's/sender's public key parameters
     * @param params        encoding and derivation parameters, may be wrapped to include an IV for an underlying block cipher.
     */
        public void Init(
            bool forEncryption,
            byte[] kdfKey,
            ParametersWithIV parameters)
        {
            _kdfKey = kdfKey;
            _forEncryption = forEncryption;
            V = new byte[0];

            _iv = parameters.GetIV();
            _iesParameters = (IesParameters) parameters.Parameters;
        }

        private byte[] EncryptBlock(
            byte[] input,
            int inOff,
            int inLen,
            byte[] macData)
        {
            byte[] c, k, k1, k2;
            int len;

            // Block cipher mode.
            k1 = new byte[((IesWithCipherParameters) _iesParameters).CipherKeySize / 8];
            k2 = new byte[_iesParameters.MacKeySize / 8];
//                k = new byte[k1.Length + k2.Length];
            k = _kdfKey;

//                _kdf.GenerateBytes(k, 0, k.Length);
            Array.Copy(k, 0, k1, 0, k1.Length);
            Array.Copy(k, k1.Length, k2, 0, k2.Length);

            // If iv provided use it to initialise the cipher
            if (_iv != null)
            {
                Cipher.Init(true, new ParametersWithIV(new KeyParameter(k1), _iv));
            }
            else
            {
                Cipher.Init(true, new KeyParameter(k1));
            }

            c = new byte[Cipher.GetOutputSize(inLen)];
            len = Cipher.ProcessBytes(input, inOff, inLen, c, 0);
            len += Cipher.DoFinal(c, len);

            // Convert the length of the encoding vector into a byte array.
            byte[] p2 = _iesParameters.GetEncodingV();

            // Apply the MAC.
            byte[] T = new byte[Mac.GetMacSize()];

            byte[] k2A;
            if (HashK2)
            {
                k2A = new byte[_hash.GetDigestSize()];
                _hash.Reset();
                _hash.BlockUpdate(k2, 0, k2.Length);
                _hash.DoFinal(k2A, 0);
            }
            else
            {
                k2A = k2;
            }

            Mac.Init(new KeyParameter(k2A));
            Mac.BlockUpdate(_iv, 0, _iv.Length);
            Mac.BlockUpdate(c, 0, c.Length);
            if (p2 != null)
            {
                Mac.BlockUpdate(p2, 0, p2.Length);
            }

            if (V.Length != 0 && p2 != null)
            {
//            byte[] L2 = new byte[4];
//            Pack.intToBigEndian(P2.Length * 8, L2, 0);
                byte[] L2 = (p2.Length * 8).ToBigEndianByteArray();
                Debug.Assert(L2.Length == 4, "expected to be 4 bytes long");
                Mac.BlockUpdate(L2, 0, L2.Length);
            }

            if (macData != null)
            {
                Mac.BlockUpdate(macData, 0, macData.Length);
            }

            Mac.DoFinal(T, 0);

            // Output the triple (V,C,T).
            byte[] Output = new byte[V.Length + len + T.Length];
            Array.Copy(V, 0, Output, 0, V.Length);
            Array.Copy(c, 0, Output, V.Length, len);
            Array.Copy(T, 0, Output, V.Length + len, T.Length);
            return Output;
        }

        private byte[] DecryptBlock(
            byte[] inEnc,
            int inOff,
            int inLen,
            byte[] macData)
        {
            byte[] M = null, k = null, k1 = null, k2 = null;
            int len;

            // Ensure that the length of the input is greater than the MAC in bytes
            if (inLen <= _iesParameters.MacKeySize / 8)
            {
                throw new InvalidCipherTextException("Length of input must be greater than the MAC");
            }

            // Block cipher mode.
            k1 = new byte[((IesWithCipherParameters) _iesParameters).CipherKeySize / 8];
            k2 = new byte[_iesParameters.MacKeySize / 8];
//                K = new byte[K1.Length + K2.Length];
            k = _kdfKey;
//                _kdf.GenerateBytes(K, 0, K.Length);
            Array.Copy(k, 0, k1, 0, k1.Length);
            Array.Copy(k, k1.Length, k2, 0, k2.Length);

            // If IV provide use it to initialize the cipher
            if (_iv != null)
            {
                Cipher.Init(false, new ParametersWithIV(new KeyParameter(k1), _iv));
            }
            else
            {
                Cipher.Init(false, new KeyParameter(k1));
            }

            M = new byte[Cipher.GetOutputSize(inLen - V.Length - Mac.GetMacSize())];
            len = Cipher.ProcessBytes(inEnc, inOff + V.Length, inLen - V.Length - Mac.GetMacSize(), M, 0);
            len += Cipher.DoFinal(M, len);

            // Convert the length of the encoding vector into a byte array.
            byte[] p2 = _iesParameters.GetEncodingV();

            // Verify the MAC.
            int end = inOff + inLen;
            byte[] t1 = Arrays.CopyOfRange(inEnc, end - Mac.GetMacSize(), end);

            byte[] t2 = new byte[t1.Length];
            byte[] k2A;
            if (HashK2)
            {
                k2A = new byte[_hash.GetDigestSize()];
                _hash.Reset();
                _hash.BlockUpdate(k2, 0, k2.Length);
                _hash.DoFinal(k2A, 0);
            }
            else
            {
                k2A = k2;
            }

            Mac.Init(new KeyParameter(k2A));
            Mac.BlockUpdate(_iv, 0, _iv.Length);
            Mac.BlockUpdate(inEnc, inOff + V.Length, inLen - V.Length - t2.Length);

            if (p2 != null)
            {
                Mac.BlockUpdate(p2, 0, p2.Length);
            }

            if (V.Length != 0 && p2 != null)
            {
//            byte[] L2 = new byte[4];
//            Pack.intToBigEndian(P2.Length * 8, L2, 0);
                byte[] L2 = (p2.Length * 8).ToBigEndianByteArray();
                Debug.Assert(L2.Length == 4, "expected to be 4 bytes long");

                Mac.BlockUpdate(L2, 0, L2.Length);
            }

            if (macData != null)
            {
                Mac.BlockUpdate(macData, 0, macData.Length);
            }

            Mac.DoFinal(t2, 0);

            if (!Arrays.ConstantTimeAreEqual(t1, t2))
            {
                throw new InvalidCipherTextException("Invalid MAC.");
            }

            // Output the message.
            return Arrays.CopyOfRange(M, 0, len);
        }

        public byte[] ProcessBlock(
            byte[] input,
            int inOff,
            int inLen,
            byte[] macData)
        {
            return _forEncryption
                ? EncryptBlock(input, inOff, inLen, macData)
                : DecryptBlock(input, inOff, inLen, macData);
        }
    }
}