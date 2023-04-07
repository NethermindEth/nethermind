// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Nethermind.Core.Extensions
{
    public static unsafe partial class Bytes
    {
        private static readonly byte[] ReverseMask = { 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 };
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
        public static void Or(this Span<byte> thisSpan, Span<byte> valueSpan)
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
                thisSpan[i] |= valueSpan[i];
            }
        }

        public static uint CountBits(this ReadOnlySpan<byte> thisSpan)
        {
            Debug.Assert(thisSpan.Length % sizeof(ulong) == 0);

            int result = 0;
            ReadOnlySpan<ulong> ulongSpan = MemoryMarshal.Cast<byte, ulong>(thisSpan);
            for (int i = 0; i < ulongSpan.Length; i++)
            {
                result += BitOperations.PopCount(ulongSpan[i]);
            }

            return (uint)result;
        }

        public static uint CountBits(this Span<byte> thisSpan)
        {
            return ((ReadOnlySpan<byte>)thisSpan).CountBits();
        }
    }
}
