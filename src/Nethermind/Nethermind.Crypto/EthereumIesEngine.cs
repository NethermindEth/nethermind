// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Utilities;

namespace Nethermind.Crypto
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
        private bool _forEncryption;
        private byte[] _kdfKey;
        private IDigest _hash;
        private IMac _mac;
        private BufferedBlockCipher _cipher;
        private IesParameters _iesParameters;
        private byte[] _iv;

        /**
     * set up for use with stream mode, where the key derivation function
     * is used to provide a stream of bytes to xor with the message.
     *  @param agree the key agreement used as the basis for the encryption
     * @param kdf    the key derivation function used for byte generation
     * @param mac    the message authentication code generator for the message
     * @param hash   hash ing function
     * @param cipher the actual cipher
     */
        public EthereumIesEngine(IMac mac, IDigest hash, BufferedBlockCipher cipher)
        {
            _mac = mac;
            _hash = hash;
            _cipher = cipher;
        }

        /**
     * Initialise the encryptor.
     *
     * @param forEncryption whether or not this is encryption/decryption.
     * @param privParam     our private key parameters
     * @param pubParam      the recipient's/sender's public key parameters
     * @param params        encoding and derivation parameters, may be wrapped to include an IV for an underlying block cipher.
     */
        public void Init(bool forEncryption, byte[] kdfKey, ParametersWithIV parameters)
        {
            _kdfKey = kdfKey;
            _forEncryption = forEncryption;
            _iv = parameters.GetIV();
            _iesParameters = (IesParameters)parameters.Parameters;
        }

        private byte[] EncryptBlock(byte[] input, int inOff, int inLen, byte[] macData)
        {
            // Block cipher mode.
            byte[] k1 = new byte[((IesWithCipherParameters)_iesParameters).CipherKeySize / 8];
            byte[] k2 = new byte[_iesParameters.MacKeySize / 8];
            byte[] k = _kdfKey;

            Array.Copy(k, 0, k1, 0, k1.Length);
            Array.Copy(k, k1.Length, k2, 0, k2.Length);

            _cipher.Init(true, new ParametersWithIV(new KeyParameter(k1), _iv));

            byte[] c = new byte[_cipher.GetOutputSize(inLen)];
            int len = _cipher.ProcessBytes(input, inOff, inLen, c, 0);
            len += _cipher.DoFinal(c, len);

            // Convert the length of the encoding vector into a byte array.
            byte[] p2 = _iesParameters.GetEncodingV();

            // Apply the MAC.
            byte[] T = new byte[_mac.GetMacSize()];

            byte[] k2A = new byte[_hash.GetDigestSize()];
            _hash.Reset();
            _hash.BlockUpdate(k2, 0, k2.Length);
            _hash.DoFinal(k2A, 0);

            _mac.Init(new KeyParameter(k2A));
            _mac.BlockUpdate(_iv, 0, _iv.Length);
            _mac.BlockUpdate(c, 0, c.Length);
            if (p2 is not null)
            {
                _mac.BlockUpdate(p2, 0, p2.Length);
            }

            if (macData is not null)
            {
                _mac.BlockUpdate(macData, 0, macData.Length);
            }

            _mac.DoFinal(T, 0);

            // Output the double (C,T).
            byte[] output = new byte[len + T.Length];
            Array.Copy(c, 0, output, 0, len);
            Array.Copy(T, 0, output, len, T.Length);
            return output;
        }

        private byte[] DecryptBlock(byte[] inEnc, int inOff, int inLen, byte[]? macData)
        {
            // Ensure that the length of the input is greater than the MAC in bytes
            if (inLen <= _iesParameters.MacKeySize / 8)
            {
                throw new InvalidCipherTextException("Length of input must be greater than the MAC");
            }

            // Block cipher mode.
            byte[] k1 = new byte[((IesWithCipherParameters)_iesParameters).CipherKeySize / 8];
            byte[] k2 = new byte[_iesParameters.MacKeySize / 8];
            byte[] k = _kdfKey;
            Array.Copy(k, 0, k1, 0, k1.Length);
            Array.Copy(k, k1.Length, k2, 0, k2.Length);

            _cipher.Init(false, new ParametersWithIV(new KeyParameter(k1), _iv));

            byte[] M = new byte[_cipher.GetOutputSize(inLen - _mac.GetMacSize())];
            int len = _cipher.ProcessBytes(inEnc, inOff, inLen - _mac.GetMacSize(), M, 0);
            len += _cipher.DoFinal(M, len);

            // Convert the length of the encoding vector into a byte array.
            byte[] p2 = _iesParameters.GetEncodingV();

            // Verify the MAC.
            int end = inOff + inLen;
            byte[] t1 = Arrays.CopyOfRange(inEnc, end - _mac.GetMacSize(), end);

            byte[] t2 = new byte[t1.Length];
            byte[] k2A = new byte[_hash.GetDigestSize()];
            _hash.Reset();
            _hash.BlockUpdate(k2, 0, k2.Length);
            _hash.DoFinal(k2A, 0);

            _mac.Init(new KeyParameter(k2A));
            _mac.BlockUpdate(_iv, 0, _iv.Length);
            _mac.BlockUpdate(inEnc, inOff, inLen - t2.Length);

            if (p2 is not null)
            {
                _mac.BlockUpdate(p2, 0, p2.Length);
            }

            if (macData is not null)
            {
                _mac.BlockUpdate(macData, 0, macData.Length);
            }

            _mac.DoFinal(t2, 0);

            if (!Arrays.ConstantTimeAreEqual(t1, t2))
            {
                throw new InvalidCipherTextException("Invalid MAC.");
            }

            // Output the message.
            return Arrays.CopyOfRange(M, 0, len);
        }

        public byte[] ProcessBlock(byte[] input, int inOff, int inLen, byte[] macData)
        {
            return _forEncryption
                ? EncryptBlock(input, inOff, inLen, macData)
                : DecryptBlock(input, inOff, inLen, macData);
        }
    }
}
