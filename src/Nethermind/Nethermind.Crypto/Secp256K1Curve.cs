// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Crypto
{
    public static class Secp256K1Curve
    {
        /*
        public static readonly BigInteger P =
            BigInteger.Pow(2, 256)
            - BigInteger.Pow(2, 32)
            - BigInteger.Pow(2, 9)
            - BigInteger.Pow(2, 8)
            - BigInteger.Pow(2, 7)
            - BigInteger.Pow(2, 6)
            - BigInteger.Pow(2, 4)
            - 1; */

        public static readonly UInt256 N = UInt256.Parse("115792089237316195423570985008687907852837564279074904382605163141518161494337");
        public static readonly UInt256 NMinusOne = N - 1;
        public static readonly UInt256 HalfN = N / 2;
        public static readonly UInt256 HalfNPlusOne = HalfN + 1;
    }
}
