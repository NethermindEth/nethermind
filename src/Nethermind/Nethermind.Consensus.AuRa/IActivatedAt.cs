// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

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

    public interface IActivatedAtForkId : IActivatedAt<ForkActivation>
    {
        void EnsureActivated(BlockHeader blockHeader)
        {
            if (Activation > (blockHeader.Number, blockHeader.Timestamp))
                throw new InvalidOperationException($"{GetType().Name} not activated: expected at block ({Activation}), got block ({blockHeader.Number}, {blockHeader.Timestamp})");
        }
    }

    internal static class ActivatedAtBlockExtensions
    {
        public static void BlockActivationCheck(this IActivatedAtBlock activatedAtBlock, BlockHeader parentHeader)
        {
            if (parentHeader.Number + 1 < activatedAtBlock.ActivationBlock)
                throw new InvalidOperationException($"{activatedAtBlock.GetType().Name} is not active for block {parentHeader.Number + 1}. Its activated on block {activatedAtBlock.ActivationBlock}.");
        }
    }
}
