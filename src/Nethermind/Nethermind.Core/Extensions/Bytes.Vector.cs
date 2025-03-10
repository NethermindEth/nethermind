// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace Nethermind.Core.Extensions
{
    public static unsafe partial class Bytes
    {
        private static readonly byte[] ReverseMask = { 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 };

        static Bytes()
        {

        }


        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Or(this Span<byte> thisSpan, ReadOnlySpan<byte> valueSpan)
        {
            var length = thisSpan.Length;
            if (length != valueSpan.Length)
            {
                throw new ArgumentException("Both byte spans has to be same length.");
            }

            int i = 0;

            fixed (byte* thisPtr = thisSpan)
            fixed (byte* valuePtr = valueSpan)
            {

            }

            for (; i < length; i++)
            {
                thisSpan[i] |= valueSpan[i];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Xor(this Span<byte> thisSpan, ReadOnlySpan<byte> valueSpan)
        {
            var length = thisSpan.Length;
            if (length != valueSpan.Length)
            {
                throw new ArgumentException("Both byte spans has to be same length.");
            }

            int i = 0;

            fixed (byte* thisPtr = thisSpan)
            fixed (byte* valuePtr = valueSpan)
            {

            }

            for (; i < length; i++)
            {
                thisSpan[i] ^= valueSpan[i];
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
