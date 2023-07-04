// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
