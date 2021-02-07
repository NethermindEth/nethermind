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
using System.Security.Cryptography;
using System.Threading;
using Nethermind.Core.Extensions;

namespace Nethermind.Crypto
{
    public class OptimizedKdf
    {
        private static readonly ThreadLocal<SHA256> _sha256 = new(SHA256.Create);

        private static readonly ThreadLocal<byte[]> _dataToHash = new(BuildDataToHash);

        private static byte[] BuildDataToHash()
        {
            byte[] counterData = BitConverter.IsLittleEndian ? Bytes.Reverse(BitConverter.GetBytes(1)) : BitConverter.GetBytes(1);
            byte[] dataToHash = new byte[36];
            counterData.AsSpan().CopyTo(dataToHash.AsSpan().Slice(0, 4));
            return dataToHash;
        }
        
        /// <summary>
        /// Performs the NIST SP 800-56 Concatenation Key Derivation Function ("KDF") to derive a key of the specified desired length from a base key of arbitrary length.
        /// </summary>
        /// <param name="key">The base key to derive another key from.</param>
        /// <returns>Returns the key derived from the provided base key and hash algorithm.</returns>
        public byte[] Derive(byte[] key)
        {
            byte[] dataToHash = _dataToHash.Value;
            key.AsSpan().CopyTo(dataToHash.AsSpan().Slice(4, 32));
            return _sha256.Value.ComputeHash(dataToHash);
        }
    }
}
