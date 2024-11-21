// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Rewards
{
    public class NoBlockRewards : IRewardCalculator, IRewardCalculatorSource
    {
        private NoBlockRewards() { }

        public static NoBlockRewards Instance { get; } = new();

        private static readonly BlockReward[] _noRewards = [];

        public BlockReward[] CalculateRewards(Block block) => _noRewards;

        public IRewardCalculator Get(ITransactionProcessor processor) => Instance;
    }
}
