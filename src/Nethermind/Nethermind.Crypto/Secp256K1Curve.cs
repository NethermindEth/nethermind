// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Crypto;

public static class Secp256K1Curve
{
    //public static readonly BigInteger P =
    //    BigInteger.Pow(2, 256)
    //    - BigInteger.Pow(2, 32)
    //    - BigInteger.Pow(2, 9)
    //    - BigInteger.Pow(2, 8)
    //    - BigInteger.Pow(2, 7)
    //    - BigInteger.Pow(2, 6)
    //    - BigInteger.Pow(2, 4)
    //    - 1;

    /// <summary>
    /// Group order for secp256k1 defined as <c>n</c> in the
    /// <see href="https://www.secg.org/sec2-v2.pdf">
    /// Standards for Efficient Cryptography, SEC 2, 2.4.1</see>.
    /// </summary>
    // fffffffffffffffffffffffffffffffebaaedce6af48a03bbfd25e8cd0364141
    // 115792089237316195423570985008687907852837564279074904382605163141518161494337
    public static readonly UInt256 N = new(13822214165235122497ul, 13451932020343611451ul, 18446744073709551614ul, 18446744073709551615ul);
    public static readonly UInt256 HalfN = N / 2;
    public static readonly UInt256 HalfNPlusOne = HalfN + 1;
}
