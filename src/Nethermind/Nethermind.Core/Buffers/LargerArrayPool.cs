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
using System.Buffers;
using System.Collections.Generic;

namespace Nethermind.Core.Buffers
{
    public sealed class LargerArrayPool : ArrayPool<byte>
    {
        static readonly LargerArrayPool s_instance = new();

        public static new ArrayPool<byte> Shared => s_instance;

        public const int LargeBufferSize = 8 * 1024 * 1024;
        const int ArrayPoolLimit = 1024 * 1024;

        // The count is estimated as a number of CPUs (similar to IEthModule) and 2 additional ones.
        // The CPU based is aligned with the SKU-like cloud environments where one scales both CPU count and an amount of memory.
        private static readonly int s_maxLargeBufferCount = Environment.ProcessorCount + 2;
        
        private readonly Stack<byte[]> _pool = new(s_maxLargeBufferCount);
        private readonly int _largeBufferSize;
        private readonly int _arrayPoolLimit;
        private readonly ArrayPool<byte> _smallPool;
        private readonly int _maxBufferCount;

        public LargerArrayPool(int arrayPoolLimit = ArrayPoolLimit, int largeBufferSize = LargeBufferSize, int? maxBufferCount = default, ArrayPool<byte>? smallPool = null)
        {
            _smallPool = smallPool ?? ArrayPool<byte>.Shared;
            _arrayPoolLimit = arrayPoolLimit;
            _largeBufferSize = largeBufferSize;
            _maxBufferCount = maxBufferCount.GetValueOrDefault(s_maxLargeBufferCount);
        }

        byte[] RentLarge()
        {
            lock (_pool)
            {
                if (_pool.TryPop(out byte[]? buffer))
                {
                    return buffer;
                }
            }

            return new byte[_largeBufferSize];
        }

        void ReturnLarge(byte[] array, bool clearArray)
        {
            if (clearArray)
            {
                Array.Clear(array, 0, array.Length);
            }

            lock (_pool)
            {
                if (_pool.Count < _maxBufferCount)
                {
                    _pool.Push(array);
                }
            }
        }

        public override byte[] Rent(int minimumLength)
        {
            if (minimumLength <= _arrayPoolLimit)
            {
                // small cases delegates to a small pool
                return _smallPool.Rent(minimumLength);
            }

            if (minimumLength <= _largeBufferSize)
            {
                // large enough handled by this pool
                return RentLarge();
            }

            // too big to pool, just allocate
            return new byte[minimumLength];
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            int length = array.Length;
            if (length <= _arrayPoolLimit)
            {
                _smallPool.Return(array, clearArray);
            }
            else if (length <= _largeBufferSize)
            {
                ReturnLarge(array, clearArray);
            }

            // arrays bigger than LargeBufferSize are not pooled
        }
    }
}
