// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.EthereumClassic;

/// <summary>
/// Reward calculator for Ethereum Classic implementing ECIP-1017 monetary policy.
/// Calculates block rewards using the era system (20% reduction every era).
/// Era period is configurable: ETC mainnet uses 5M blocks, Mordor uses 2M blocks.
/// This operates independently of the spec system to avoid Fork ID conflicts per ECIP-1082.
/// </summary>
public class EtcRewardCalculator : IRewardCalculator, IRewardCalculatorSource
{
    private readonly long _eraPeriod;
    private static readonly UInt256 BaseReward = 5_000_000_000_000_000_000; // 5 ETC in wei

    public EtcRewardCalculator(long eraPeriod)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(eraPeriod);
        _eraPeriod = eraPeriod;
    }

    /// <summary>
    /// Calculates the block reward for a given block number according to ECIP-1017.
    /// Era 1: blocks 1 to eraPeriod → 5 ETC
    /// Era 2: blocks eraPeriod+1 to 2*eraPeriod → 4 ETC (5 * 0.8)
    /// Era N: 5 ETC * (4/5)^(N-1)
    /// </summary>
    private UInt256 GetBlockReward(long blockNumber)
    {
        if (blockNumber <= 0)
        {
            return BaseReward;
        }

        // Era is 1-indexed: Era 1 = blocks 1-eraPeriod, Era 2 = blocks eraPeriod+1-2*eraPeriod, etc.
        // For calculation, we use 0-indexed era: era0 = (blockNumber - 1) / _eraPeriod
        long era = (blockNumber - 1) / _eraPeriod;

        // Calculate reward = 5 ETC * (4/5)^era using integer math
        // To avoid overflow, we apply the reduction iteratively
        UInt256 reward = BaseReward;
        for (long i = 0; i < era; i++)
        {
            // reward = reward * 4 / 5
            reward = reward * 4 / 5;
        }

        return reward;
    }

    public BlockReward[] CalculateRewards(Block block)
    {
        if (block.IsGenesis)
        {
            return [];
        }

        UInt256 blockReward = GetBlockReward(block.Number);
        BlockReward[] rewards = new BlockReward[1 + block.Uncles.Length];

        // Nephew reward (bonus for including uncles) is 1/32 per uncle in all eras
        BlockHeader blockHeader = block.Header;
        UInt256 mainReward = blockReward + (uint)block.Uncles.Length * (blockReward >> 5);
        rewards[0] = new BlockReward(blockHeader.Beneficiary, mainReward);

        // Era determines uncle reward formula
        long era = (block.Number - 1) / _eraPeriod;

        for (int i = 0; i < block.Uncles.Length; i++)
        {
            UInt256 uncleReward = GetUncleReward(blockReward, blockHeader, block.Uncles[i], era);
            rewards[i + 1] = new BlockReward(block.Uncles[i].Beneficiary, uncleReward, BlockRewardType.Uncle);
        }

        return rewards;
    }

    private static UInt256 GetUncleReward(UInt256 blockReward, BlockHeader blockHeader, BlockHeader uncle, long era)
    {
        if (era == 0)
        {
            // Era 1: Standard Ethereum uncle reward formula
            // Uncle reward = blockReward * (8 - distance) / 8
            return blockReward - ((uint)(blockHeader.Number - uncle.Number) * blockReward >> 3);
        }

        // Era 2+: ECIP-1017 changed uncle reward to fixed 1/32 of block reward
        return blockReward >> 5;
    }

    public IRewardCalculator Get(ITransactionProcessor processor) => this;
}
