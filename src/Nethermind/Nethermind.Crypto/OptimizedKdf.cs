// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            counterData.AsSpan().CopyTo(dataToHash.AsSpan(0, 4));
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
            key.AsSpan().CopyTo(dataToHash.AsSpan(4, 32));
            return _sha256.Value.ComputeHash(dataToHash);
        }
    }
}
