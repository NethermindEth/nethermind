// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Runtime.CompilerServices;

namespace Nethermind.Core
{
    public static class MemorySizes
    {
        private const int AlignmentMask = 7;
        public const int Alignment = 8;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Align(long unalignedSize)
        {
            return unalignedSize + (-unalignedSize & AlignmentMask);
        }

        public const int RefSize = 8;

        public const int SmallObjectOverhead = 24;

        public const int SmallObjectFreeDataSize = 8;

        // public const int LargeObjectOverhead = 32; // just guessing, 20 on 32bit
        public const int ArrayOverhead = 24;

        private static BitArray _isPrime = ESieve(1024 * 1024); // 1MB in memory

        public static int FindNextPrime(int number)
        {
            number++;
            for (; number < _isPrime.Length; number++)
                //found a prime return that number
                if (_isPrime[number])
                    return number;
            //no prime return error code
            return -1;
        }

        /// <summary>
        /// https://stackoverflow.com/questions/23644479/find-next-prime-number
        /// </summary>
        /// <param name="upperLimit"></param>
        /// <returns></returns>
        public static BitArray ESieve(int upperLimit)
        {
            int sieveBound = (int)(upperLimit - 1);
            int upperSqrt = (int)Math.Sqrt(sieveBound);
            BitArray primeBits = new(sieveBound + 1, true);
            primeBits[0] = false;
            primeBits[1] = false;
            for (int j = 4; j <= sieveBound; j += 2)
            {
                primeBits[j] = false;
            }

            for (int i = 3; i <= upperSqrt; i += 2)
            {
                if (primeBits[i])
                {
                    int inc = i * 2;
                    for (int j = i * i; j <= sieveBound; j += inc)
                    {
                        primeBits[j] = false;
                    }
                }
            }

            return primeBits;
        }
    }
}
