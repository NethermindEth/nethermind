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

using System;
using System.Runtime.CompilerServices;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Ssz
{
    public struct SszStream
    {
        public byte[] Data { get; private set; }

        public int Position { get; set; }
        
        public SszStream(int length)
        {
            Data = new byte[length];
            Position = 0;
        }
        
        public static void Encode(byte value)
        {
            throw new NotImplementedException();
        }
        
        public static void Encode(ushort value)
        {
            throw new NotImplementedException();
        }
        
        public static void Encode(uint value)
        {
            throw new NotImplementedException();
        }
        
        public static void Encode(ulong value)
        {
            throw new NotImplementedException();
        }
        
        public static void Encode(UInt128 value)
        {
            throw new NotImplementedException();
        }
        
        public static void Encode(UInt256 value)
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Encode(bool value)
        {
            WriteByte(value ? (byte) 0 : (byte) 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteByte(byte value)
        {
            Data[Position++] = value;
        }
    }
}