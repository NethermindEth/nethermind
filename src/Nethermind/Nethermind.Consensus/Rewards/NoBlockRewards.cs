// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Rewards
{
    public class NoBlockRewards : IRewardCalculator, IRewardCalculatorSource
    {
        private NoBlockRewards() { }

        public static NoBlockRewards Instance { get; } = new();

        private static readonly BlockReward[] _noRewards = Array.Empty<BlockReward>();

        public BlockReward[] CalculateRewards(Block block) => _noRewards;
        public BlockReward[] CalculateRewards(Block block, IBlockTracer tracer) => CalculateRewards(block);

        public IRewardCalculator Get(ITransactionProcessor processor) => Instance;
    }
}
