// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
                result = result.PadLeft(outputLength, bigInteger.Sign < 0 ? (byte)0xff : (byte)0x00);
            }

            return result;
        }
    }
}
