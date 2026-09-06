// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core;

namespace Nethermind.Consensus.AuRa
{
    public interface IActivatedAt<out T> where T : IComparable<T>
    {
        T Activation { get; }
    }

    public interface IActivatedAt : IActivatedAt<ulong>
    {
    }

    public interface IActivatedAtBlock : IActivatedAt
    {
        public ulong ActivationBlock => Activation;
    }

    /// <summary>
    /// Compares activated items by their activation value.
    /// </summary>
    /// <typeparam name="T">The activated item type.</typeparam>
    /// <typeparam name="TActivation">The activation value type.</typeparam>
    public readonly struct ActivatedAtComparer<T, TActivation> : IComparer<T>
        where T : IActivatedAt<TActivation>
        where TActivation : IComparable<TActivation>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(T? x, T? y) => x is not null ? y is not null ? x.Activation.CompareTo(y.Activation) : 1 : y is null ? 0 : -1;
    }

    internal static class ActivatedAtBlockExtensions
    {
        public static void BlockActivationCheck(this IActivatedAtBlock activatedAtBlock, BlockHeader parentHeader)
        {
            if (parentHeader.Number + 1 < activatedAtBlock.ActivationBlock) throw new InvalidOperationException($"{activatedAtBlock.GetType().Name} is not active for block {parentHeader.Number + 1}. Its activated on block {activatedAtBlock.ActivationBlock}.");
        }
    }
}
