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

using System.Numerics;

namespace Nethermind.Core.Extensions
{
    public static class BigIntegerExtensions
    {
        public static byte[] ToBigEndianByteArray(this BigInteger bigInteger, int outputLength = -1)
        {
            if (outputLength == 0)
            {
                return Bytes.Empty;
            }
            
            byte[] result = bigInteger.ToByteArray(false, true);
            if (result[0] == 0 && result.Length != 1)
            {
                result = result.Slice(1, result.Length - 1);
            }

            if (outputLength != -1)
            {
                result = result.PadLeft(outputLength, bigInteger.Sign < 0 ? (byte) 0xff : (byte) 0x00);
            }

            return result;
        }
    }
}
