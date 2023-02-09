// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;

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

        public static readonly BigInteger N = BigInteger.Parse("115792089237316195423570985008687907852837564279074904382605163141518161494337");

        public static readonly BigInteger HalfN = N / 2;
    }
}
