// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Core;

namespace Nethermind.Consensus.AuRa
{
    public interface IActivatedAt<out T> where T : IComparable<T>
    {
        T Activation { get; }
    }

    public interface IActivatedAt : IActivatedAt<long>
    {
    }

    public interface IActivatedAtBlock : IActivatedAt
    {
        public long ActivationBlock => Activation;
    }

    internal static class ActivatedAtBlockExtensions
    {
        public static void BlockActivationCheck(this IActivatedAtBlock activatedAtBlock, BlockHeader parentHeader)
        {
            if (parentHeader.Number + 1 < activatedAtBlock.ActivationBlock) throw new InvalidOperationException($"{activatedAtBlock.GetType().Name} is not active for block {parentHeader.Number + 1}. Its activated on block {activatedAtBlock.ActivationBlock}.");
        }
    }
}
