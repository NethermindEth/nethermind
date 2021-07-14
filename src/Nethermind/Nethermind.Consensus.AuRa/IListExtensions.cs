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
using System.Collections.Generic;
using Nethermind.Core.Collections;

namespace Nethermind.Consensus.AuRa
{
    public static class ListExtensions
    {
        /// <summary>
        /// Tries to get a <see cref="IActivatedAt"/> item for block <see cref="activation"/>.
        /// </summary>
        /// <param name="list"></param>
        /// <param name="activation"></param>
        /// <param name="item"></param>
        /// <typeparam name="T"></typeparam>
        /// <typeparam name="TComparable"></typeparam>
        /// <returns></returns>
        public static bool TryGetForActivation<T, TComparable>(this IList<T> list, in TComparable activation, out T item) where T : IActivatedAt<TComparable> where TComparable : IComparable<TComparable> => 
            list.TryGetSearchedItem(activation, (b, c) => b.CompareTo(c.Activation), out item);
        
        public static bool TryGetForBlock<T>(this IList<T> list, in long blockNumber, out T item) where T : IActivatedAtBlock =>
            list.TryGetSearchedItem(blockNumber, (b, c) => b.CompareTo(c.ActivationBlock), out item);
    }
}
