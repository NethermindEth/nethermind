// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security.Cryptography;
using Nethermind.Core.Attributes;

namespace Nethermind.Crypto
{
    [RequiresSecurityReview("Analyze RNGCryptoServiceProvider quality and its behaviour on reuse")]
    public class CryptoRandom : ICryptoRandom
    {
        private readonly RandomNumberGenerator _secureRandom = RandomNumberGenerator.Create();

        public void GenerateRandomBytes(Span<byte> bytes)
        {
            _secureRandom.GetBytes(bytes);
        }

        public byte[] GenerateRandomBytes(int length)
        {
            byte[] bytes = new byte[length];
            _secureRandom.GetBytes(bytes);
            return bytes;
        }

        public int NextInt(int max)
        {
            // Use cryptographically secure RNG; preserve Random.Next behavior for non-positive max
            // (Random.Next(0) returns 0; negatives throw). This keeps compatibility.
            return max <= 0 ? 0 : RandomNumberGenerator.GetInt32(max);
        }

        public void Dispose()
        {
            _secureRandom.Dispose();
        }
    }
}
