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

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Nethermind.Core.Extensions
{
    public static unsafe partial class Bytes
    {
        private static readonly byte[] ReverseMask = {15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0};
        private static readonly Vector256<byte> ReverseMaskVec;

        static Bytes()
        {
            if (Avx2.IsSupported)
            {
                fixed (byte* ptr_mask = ReverseMask)
                {
                    ReverseMaskVec = Avx2.LoadVector256(ptr_mask);
                }
            }
        }

        public static void Avx2Reverse256InPlace(Span<byte> bytes)
        {
            fixed (byte* inputPointer = bytes)
            {
                Vector256<byte> inputVector = Avx2.LoadVector256(inputPointer);
                Vector256<byte> resultVector = Avx2.Shuffle(inputVector, ReverseMaskVec);
                resultVector = Avx2.Permute4x64(resultVector.As<byte, ulong>(), 0b01001110).As<ulong, byte>();

                Avx2.Store(inputPointer, resultVector);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Or(this Span<byte> thisSpam, Span<byte> valueSpam)
        {
            var length = thisSpam.Length;
            if (length != valueSpam.Length)
            {
                throw new ArgumentException("Both byte spans has to be same length.");
            }

            int i = 0;

            fixed (byte* thisPtr = thisSpam)
            fixed (byte* valuePtr = valueSpam)
            {
                if (Avx2.IsSupported)
                {
                    for (; i < length - (Vector256<byte>.Count - 1); i += Vector256<byte>.Count)
                    {
                        Vector256<byte> b1 = Avx2.LoadVector256(thisPtr + i);
                        Vector256<byte> b2 = Avx2.LoadVector256(valuePtr + i);
                        Avx2.Store(thisPtr + i, Avx2.Or(b1, b2));
                    }
                }
                else if (Sse2.IsSupported)
                {
                    for (; i < length - (Vector128<byte>.Count - 1); i += Vector128<byte>.Count)
                    {
                        Vector128<byte> b1 = Sse2.LoadVector128(thisPtr + i);
                        Vector128<byte> b2 = Sse2.LoadVector128(valuePtr + i);
                        Sse2.Store(thisPtr + i, Sse2.Or(b1, b2));
                    }
                }
            }

            for (; i < length; i++)
            {
                thisSpam[i] |= valueSpam[i];
            }
        }

        public static uint CountBits(this Span<byte> thisSpan)
        {
            uint result = 0;
            if (Popcnt.IsSupported)
            {
                Span<uint> uintSpam = MemoryMarshal.Cast<byte, uint>(thisSpan);
                for (int i = 0; i < uintSpam.Length; i++)
                {
                    result += Popcnt.PopCount(uintSpam[i]);
                }
            }
            else
            {
                for (int i = 0; i < thisSpan.Length; i++)
                {
                    int n = thisSpan[i];
                    while (n > 0)
                    {
                        n &= n - 1;
                        result++;
                    }
                }
            }

            return result;
        }
    }
}
