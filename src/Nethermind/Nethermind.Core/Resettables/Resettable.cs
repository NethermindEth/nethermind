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
using System.Buffers;
using System.Collections.Generic;

namespace Nethermind.Core.Resettables
{
    public static class Resettable
    {
        public const int ResetRatio = 2;
        public const int StartCapacity = 64;
        public const int EmptyPosition = -1;
    }

    public static class Resettable<T>
    {
        private const int ResetRatio = Resettable.ResetRatio;
        private const int StartCapacity = Resettable.StartCapacity;

        private static ArrayPool<T> _arrayPool = ArrayPool<T>.Shared;

        public static void IncrementPosition(ref T[] array, ref int currentCapacity, ref int currentPosition)
        {
            currentPosition++;
            while (currentPosition >= currentCapacity - 1) // sometimes we ask about the _currentPosition + 1;
            {
                currentCapacity *= ResetRatio;
            }

            if (currentCapacity > array.Length)
            {
                T[] oldArray = array;
                array = _arrayPool.Rent(currentCapacity);
                Array.Copy(oldArray, array, oldArray.Length);
                oldArray.AsSpan().Clear();
                _arrayPool.Return(oldArray);
            }
        }

        public static void Reset(ref T[] array, ref int currentCapacity, ref int currentPosition, int startCapacity = StartCapacity)
        {
            array.AsSpan().Clear();
            if (currentPosition < currentCapacity / ResetRatio && currentCapacity > startCapacity)
            {
                _arrayPool.Return(array);
                currentCapacity = Math.Max(startCapacity, currentCapacity / ResetRatio);
                array = _arrayPool.Rent(currentCapacity);    
            }

            currentPosition = Resettable.EmptyPosition;
        }
    }
}
