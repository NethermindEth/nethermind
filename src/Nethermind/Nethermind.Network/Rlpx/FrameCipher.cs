// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

using Nethermind.Crypto;

using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace Nethermind.Network.Rlpx
{
    public class FrameCipher : IFrameCipher
    {
        private const int BlockSize = 16;
        private const int KeySize = 32;

        private readonly IBufferedCipher _decryptionCipher;
        private readonly IBufferedCipher _encryptionCipher;

        public FrameCipher(byte[] aesKey)
        {
            IBlockCipher aes = AesEngineX86Intrinsic.IsSupported ? new AesEngineX86Intrinsic() : new AesEngine();

            Debug.Assert(aesKey.Length == KeySize, $"AES key expected to be {KeySize} bytes long");

            _encryptionCipher = new BufferedBlockCipher(new SicBlockCipher(aes));
            _encryptionCipher.Init(true, new ParametersWithIV(ParameterUtilities.CreateKeyParameter("AES", aesKey), new byte[BlockSize]));

            _decryptionCipher = new BufferedBlockCipher(new SicBlockCipher(aes));
            _decryptionCipher.Init(false, new ParametersWithIV(ParameterUtilities.CreateKeyParameter("AES", aesKey), new byte[BlockSize]));
        }

        public void Encrypt(byte[] input, int offset, int length, byte[] output, int outputOffset)
        {
            _encryptionCipher.ProcessBytes(input, offset, length, output, outputOffset);
        }

        public void Decrypt(byte[] input, int offset, int length, byte[] output, int outputOffset)
        {
            _decryptionCipher.ProcessBytes(input, offset, length, output, outputOffset);
        }
    }
}
