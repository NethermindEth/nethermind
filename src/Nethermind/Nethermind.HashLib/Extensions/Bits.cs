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

using System.Diagnostics;

namespace Nethermind.HashLib.Extensions
{
    internal static class Bits
    {
        static public uint RotateLeft(uint a_uint, int a_n)
        {
            Debug.Assert(a_n >= 0);

            return (uint)((a_uint << a_n) | (a_uint >> (32 - a_n)));
        }

        static public uint RotateRight(uint a_uint, int a_n)
        {
            Debug.Assert(a_n >= 0);

            return (uint)((a_uint >> a_n) | (a_uint << (32 - a_n)));
        }

        static public ulong RotateRight(ulong a_ulong, int a_n)
        {
            Debug.Assert(a_n >= 0);

            return (ulong)((a_ulong >> a_n) | (a_ulong << (64 - a_n)));
        }
    }
}
