// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Ethereum.Trie.Test
{
    /// <summary>
    /// https://stackoverflow.com/questions/11208446/generating-permutations-of-a-set-most-efficiently
    /// EO: 2016-04-14
    /// Generator of all permutations of an array of anything.
    /// Base on Heap's Algorithm. See: https://en.wikipedia.org/wiki/Heap%27s_algorithm#cite_note-3
    /// </summary>
    public static class Permutations
    {
        /// <summary>
        /// Heap's algorithm to find all pmermutations. Non recursive, more efficient.
        /// </summary>
        /// <param name="items">Items to permute in each possible ways</param>
        /// <param name="funcExecuteAndTellIfShouldStop"></param>
        /// <returns>Return true if canceled</returns> 
        public static bool ForAllPermutation<T>(T[] items, Func<T[], bool> funcExecuteAndTellIfShouldStop)
        {
            int countOfItem = items.Length;

            if (countOfItem <= 1)
            {
                return funcExecuteAndTellIfShouldStop(items);
            }

            var indexes = new int[countOfItem];
            for (int i = 0; i < countOfItem; i++)
            {
                indexes[i] = 0;
            }

            if (funcExecuteAndTellIfShouldStop(items))
            {
                return true;
            }

            for (int i = 1; i < countOfItem;)
            {
                if (indexes[i] < i)
                {
                    if ((i & 1) == 1)
                    {
                        (items[i], items[indexes[i]]) = (items[indexes[i]], items[i]);
                    }
                    else
                    {
                        (items[i], items[0]) = (items[0], items[i]);
                    }

                    if (funcExecuteAndTellIfShouldStop(items))
                    {
                        return true;
                    }

                    indexes[i]++;
                    i = 1;
                }
                else
                {
                    indexes[i++] = 0;
                }
            }

            return false;
        }
    }
}
