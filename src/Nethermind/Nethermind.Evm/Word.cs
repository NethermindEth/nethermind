//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Runtime.InteropServices;

namespace Nethermind.Evm
{
    [StructLayout(LayoutKind.Explicit, Size = Size)]
    public struct Word
    {
        public const int Size = 32;

        [FieldOffset(0)]
        public ulong U0;
        
        [FieldOffset(8)]
        public ulong U1;
        
        [FieldOffset(16)]
        public ulong U2;
        
        [FieldOffset(24)]
        public ulong U3;

        [FieldOffset(Size - sizeof(byte))]
        public byte LastByte;

        [FieldOffset(Size - sizeof(int))]
        public int LastInt;
    }
}
