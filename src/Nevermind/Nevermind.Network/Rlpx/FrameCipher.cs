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
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace Nevermind.Network.Rlpx
{
    public class FrameCipher : IFrameCipher
    {
        // TODO: check, EthereumJ suggest a block size of 32 bytes while AES should have a 16 bytes block size
        private const int BlockSize = 16;

        private const int KeySize = 32;

        private readonly IBufferedCipher _decryptionCipher;
        private readonly IBufferedCipher _encryptionCipher;

        public FrameCipher(byte[] aesKey)
        {
            Debug.Assert(aesKey.Length == KeySize, $"AES key expected to be {KeySize} bytes long");

            _encryptionCipher = CipherUtilities.GetCipher("AES/CTR/NoPadding");
            _encryptionCipher.Init(true, new ParametersWithIV(ParameterUtilities.CreateKeyParameter("AES", aesKey), new byte[BlockSize]));

            _decryptionCipher = CipherUtilities.GetCipher("AES/CTR/NoPadding");
            _decryptionCipher.Init(false, new ParametersWithIV(ParameterUtilities.CreateKeyParameter("AES", aesKey), new byte[BlockSize]));
        }

        public void Encrypt(byte[] input, int offset, int length, byte[] output, int outputOffset)
        {
            // TODO: find out the reason for ProcessBytes 
            _encryptionCipher.ProcessBytes(input, offset, length);
            byte[] enc = _encryptionCipher.DoFinal();
            Buffer.BlockCopy(enc, 0, output, outputOffset, length);
        }

        public void Decrypt(byte[] input, int offset, int length, byte[] output, int outputOffset)
        {
            _decryptionCipher.ProcessBytes(input, offset, length);
            byte[] dec = _decryptionCipher.DoFinal();
            Buffer.BlockCopy(dec, 0, output, outputOffset, length);
        }
    }
}