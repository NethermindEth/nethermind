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
        private readonly Random _random = new();

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

        [RequiresSecurityReview("There should be no unsecured method in a class that suggests security")]
        public int NextInt(int max)
        {
            return _random.Next(max);
        }

        public void Dispose()
        {
            _secureRandom.Dispose();
        }
    }
}
