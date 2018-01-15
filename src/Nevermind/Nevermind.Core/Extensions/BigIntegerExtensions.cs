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
using System.Numerics;

namespace Nevermind.Core.Extensions
{
    public static class BigIntegerExtensions
    {
        public static BigInteger Abs(this BigInteger @this)
        {
            return BigInteger.Abs(@this);
        }

        public static byte[] ToBigEndianByteArray(this BigInteger bigInteger, int outputLength = -1)
        {
            byte[] fromBigInteger = bigInteger.ToByteArray();
            int trailingZeros = fromBigInteger.TrailingZerosCount();
            if (fromBigInteger.Length == trailingZeros)
            {
                return new byte[outputLength == -1 ? 1 : outputLength];
            }

            byte[] result = new byte[fromBigInteger.Length - trailingZeros];
            for (int i = 0; i < result.Length; i++)
            {
                result[fromBigInteger.Length - trailingZeros - 1 - i] = fromBigInteger[i];
            }

            if (bigInteger.Sign < 0 && outputLength != -1)
            {
                byte[] newResult = new byte[outputLength];
                Buffer.BlockCopy(result, 0, newResult, outputLength - result.Length, result.Length);
                for (int i = 0; i < outputLength - result.Length; i++)
                {
                    newResult[i] = 0xff;
                }

                return newResult;
            }

            if (outputLength != -1)
            {
                return result.PadLeft(outputLength);
            }

            return result;
        }
    }
}