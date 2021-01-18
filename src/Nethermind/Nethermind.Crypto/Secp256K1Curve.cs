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
