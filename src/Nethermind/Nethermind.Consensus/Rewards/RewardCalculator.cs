// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Consensus.Rewards
{
    public class RewardCalculator : IRewardCalculator, IRewardCalculatorSource
    {
        private readonly ISpecProvider _specProvider;

        public RewardCalculator(ISpecProvider? specProvider)
        {
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        }

        private UInt256 GetBlockReward(Block block)
        {
            IReleaseSpec spec = _specProvider.GetSpec(block.Header);
            return spec.BlockReward;
        }

        public BlockReward[] CalculateRewards(Block block)
        {
            if (block.IsGenesis)
            {
                return Array.Empty<BlockReward>();
            }

            UInt256 blockReward = GetBlockReward(block);
            BlockReward[] rewards = new BlockReward[1 + block.Uncles.Length];

            BlockHeader blockHeader = block.Header;
            UInt256 mainReward = blockReward + (uint)block.Uncles.Length * (blockReward >> 5);
            rewards[0] = new BlockReward(blockHeader.Beneficiary, mainReward);

            for (int i = 0; i < block.Uncles.Length; i++)
            {
                UInt256 uncleReward = GetUncleReward(blockReward, blockHeader, block.Uncles[i]);
                rewards[i + 1] = new BlockReward(block.Uncles[i].Beneficiary, uncleReward, BlockRewardType.Uncle);
            }

            return rewards;
        }

        private UInt256 GetUncleReward(UInt256 blockReward, BlockHeader blockHeader, BlockHeader uncle)
        {
            return blockReward - ((uint)(blockHeader.Number - uncle.Number) * blockReward >> 3);
        }

        public IRewardCalculator Get(ITransactionProcessor processor) => this;
    }
}
