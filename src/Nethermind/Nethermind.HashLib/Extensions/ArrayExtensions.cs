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
using System.Diagnostics;

namespace Nethermind.HashLib.Extensions
{
    [DebuggerStepThrough]
    internal static class ArrayExtensions
    {
        /// <summary>
        /// Clear array with zeroes.
        /// </summary>
        /// <param name="a_array"></param>
        public static void Clear<T>(this T[] a_array, T a_value = default(T))
        {
            for (int i = 0; i < a_array.Length; i++)
                a_array[i] = a_value;
        }

        /// <summary>
        /// Clear array with zeroes.
        /// </summary>
        /// <param name="a_array"></param>
        public static void Clear<T>(this T[,] a_array, T a_value = default(T))
        {
            for (int x = 0; x < a_array.GetLength(0); x++)
            {
                for (int y = 0; y < a_array.GetLength(1); y++)
                {
                    a_array[x, y] = a_value;
                }
            }
        }

        /// <summary>
        /// Return array stated from a_index and with a_count legth.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="a_array"></param>
        /// <param name="a_index"></param>
        /// <param name="a_count"></param>
        /// <returns></returns>
        public static T[] SubArray<T>(this T[] a_array, int a_index, int a_count)
        {
            T[] result = new T[a_count];
            Array.Copy(a_array, a_index, result, 0, a_count);
            return result;
        }
    }
}
