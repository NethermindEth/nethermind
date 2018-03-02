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
using System.Security.Cryptography;

namespace Nethermind.Core.Crypto
{
    public class CryptoRandom : ICryptoRandom
    {
        private readonly RandomNumberGenerator _secureRandom = new RNGCryptoServiceProvider();
        private readonly Random _random = new Random(); // TODO: check where it is used via NextInt and review if carries any security risks

        public byte[] GenerateRandomBytes(int length)
        {
            byte[] bytes = new byte[length];
            _secureRandom.GetBytes(bytes);
            return bytes;
        }

        public int NextInt(int max)
        {   
            return _random.Next(max);
        }
    }
}