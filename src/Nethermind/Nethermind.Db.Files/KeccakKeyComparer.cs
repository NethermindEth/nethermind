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
// 

using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Nethermind.Db.Files
{
    class KeccakKeyComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y)
        {
            if (x is null)
            {
                return y is null;
            }

            if (y is null)
            {
                return false;
            }

            ref byte xb = ref x[0];
            ref byte yb = ref y[0];

            if (Unsafe.ReadUnaligned<long>(ref xb) != Unsafe.ReadUnaligned<long>(ref yb))
                return false;

            if (Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref xb, sizeof(long))) != Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref yb, sizeof(long))))
                return false;

            if (Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref xb, 2 * sizeof(long))) != Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref yb, 2 * sizeof(long))))
                return false;

            return Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref xb, 3 * sizeof(long))) ==
                   Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref yb, 3 * sizeof(long)));
        }

        public int GetHashCode(byte[] obj) => Unsafe.ReadUnaligned<int>(ref obj[0]);
    }
}
