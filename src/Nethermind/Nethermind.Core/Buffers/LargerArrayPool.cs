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

using System;
using System.Buffers;
using System.Threading;

namespace Nethermind.Core.Buffers
{
    public sealed class LargerArrayPool : ArrayPool<byte>
    {
        static readonly LargerArrayPool instance = new LargerArrayPool();

        public static new ArrayPool<byte> Shared => instance;

        public const int LargeBufferSize = 8 * 1024 * 1024;
        const int ArrayPoolLimit = 1024 * 1024;

        private byte[]? s_buffer1;
        private byte[]? s_buffer2;

        byte[] RentLarge()
        {
            return Interlocked.Exchange(ref s_buffer1, null) ??
                   Interlocked.Exchange(ref s_buffer2, null) ??
                   new byte[LargeBufferSize];
        }

        void ReturnLarge(byte[] array, bool clearArray)
        {
            if (clearArray)
            {
                Array.Clear(array, 0, array.Length);
            }

            if (Interlocked.CompareExchange(ref s_buffer1, array, null) != null)
            {
                Interlocked.CompareExchange(ref s_buffer2, array, null);
            }
        }

        public override byte[] Rent(int minimumLength)
        {
            if (minimumLength <= ArrayPoolLimit)
            {
                return ArrayPool<byte>.Shared.Rent(minimumLength);
            }

            if (minimumLength <= LargeBufferSize)
            {
                return RentLarge();
            }

            // any other case delegated to the shared
            return new byte[minimumLength];
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            int length = array.Length;
            if (length <= ArrayPoolLimit)
            {
                ArrayPool<byte>.Shared.Return(array, clearArray);

            }
            else if (length <= LargeBufferSize)
            {
                ReturnLarge(array, clearArray);
            }

            // arrays bigger than LargeBufferSize are not pooled
        }
    }
}
