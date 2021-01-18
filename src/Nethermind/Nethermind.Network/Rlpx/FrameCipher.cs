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

using System.Diagnostics;
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
            AesEngine aes = new AesEngine();
            
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
