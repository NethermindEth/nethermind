// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;

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
