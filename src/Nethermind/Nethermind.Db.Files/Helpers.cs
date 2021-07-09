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

using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace Nethermind.Db.Files
{
    static class Helpers
    {
        public enum Alignment
        {
            Eight = 8
        }

        public static int Log2(this int value) => 31 - (int)Lzcnt.LeadingZeroCount((uint)value);

        /// <summary>
        /// Aligns to the specific boundary.
        /// </summary>
        /// <returns>The aligned value.</returns>
        public static int Align(int value, int alignment)
        {
            return (value + (alignment - 1)) & -alignment;
        }

        public static IntPtr AllocAlignedMemory(int cb)
        {
            unsafe
            {
                int align = IntPtr.Size;

                IntPtr block = Marshal.AllocHGlobal(checked(cb + sizeof(IntPtr) + (align - 1)));

                // Align the pointer
                IntPtr aligned = (IntPtr)((nint)(block + sizeof(IntPtr) + (align - 1)) & ~(align - 1));

                // Store the pointer to the memory block to free right before the aligned pointer 
                *(((IntPtr*)aligned) - 1) = block;

                return aligned;
            }
        }

        public static unsafe void FreeAlignedMemory(IntPtr p)
        {
            if (p != IntPtr.Zero) 
                Marshal.FreeHGlobal(*(((IntPtr*)p) - 1));
        }
    }
}
