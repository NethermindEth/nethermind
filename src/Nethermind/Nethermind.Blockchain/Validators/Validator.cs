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

using System.Numerics;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.Validators
{
    public abstract class Validator
    {
        private static readonly BigInteger P256 = BigInteger.Pow(2, 256);
        
        private static readonly BigInteger P64 = BigInteger.Pow(2, 64);

        private static readonly BigInteger P5 = BigInteger.Pow(2, 5);

        public static bool IsInP256(UInt256 value) => true;
        
        public static bool IsInP256(BigInteger value)
        {
            return value >= BigInteger.Zero && value < P256;
        }
        
        public static bool IsInP64(BigInteger value)
        {
            return value >= BigInteger.Zero && value < P64;
        }

        public static bool IsInP5(BigInteger value)
        {
            return value >= BigInteger.Zero && value < P5;
        }
    }
}