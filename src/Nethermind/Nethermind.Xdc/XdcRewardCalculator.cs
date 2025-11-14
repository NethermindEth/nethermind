// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Xdc.Spec;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Nethermind.Xdc
{
    /// <summary>
    /// Rewards are distributed at epoch boundaries (every 900 blocks) based on:
    /// - Masternode signature count during the epoch
    /// - 40% infrastructure / 50% staking / 10% foundation split
    /// - Proportional distribution among delegators based on stake
    /// </summary>
    public class XdcRewardCalculator(
        IEpochSwitchManager epochSwitchManager,
        ISpecProvider specProvider,
        ILogManager logManager,
        IXdcSignatureTracker signatureTracker,
        IXdcStakingManager stakingManager) : IRewardCalculator
    {
        private readonly ILogger logger = logManager?.GetClassLogger() ?? NullLogger.Instance;

        // Reward amount per epoch (5000 XDC in Wei)
        // 1 XDC = 10^18 Wei, so 5000 XDC = 5000 * 10^18 Wei
        private static readonly UInt256 EPOCH_REWARD = UInt256.Parse("5000000000000000000000");

        /// <summary>
        /// Calculates block rewards according to XDPoS consensus rules.
        /// 
        /// For XDPoS, rewards are only distributed at epoch checkpoints (blocks where number % 900 == 0).
        /// At these checkpoints, rewards are calculated based on masternode signature counts during
        /// the previous epoch and distributed according to the 40/50/10 split model.
        /// </summary>
        /// <param name="block">The block to calculate rewards for</param>
        /// <returns>Array of BlockReward objects for all reward recipients</returns>
        public BlockReward[] CalculateRewards(Block block)
        {
            if (block is null)
                throw new ArgumentNullException(nameof(block));
            if (block.Header is not XdcBlockHeader xdcHeader)
                throw new InvalidOperationException("Only supports XDC headers");

            IXdcReleaseSpec spec = specProvider.GetXdcSpec(xdcHeader, xdcHeader.ExtraConsensusData.BlockRound);

            if (!epochSwitchManager.IsEpochSwitchAtBlock(xdcHeader))
            {
                // Non-checkpoint blocks don't trigger reward distribution in XDPoS
                // The block producer still gets transaction fees, but no block reward
                if (logger.IsTrace) logger.Trace($"Block {block.Number} is not an epoch switch, no rewards distributed");
                return Array.Empty<BlockReward>();
            }

            // Calculate the epoch that just completed
            long completedEpoch = (block.Number / spec.EpochLength) - 1;
            long epochStartBlock = completedEpoch * spec.EpochLength + 1;
            long epochEndBlock = (completedEpoch + 1) * spec.EpochLength;

            var epochNumber = spec.SwitchEpoch + (long)xdcHeader.ExtraConsensusData.BlockRound / spec.EpochLength;

            var originReward = spec.BlockReward * Unit.Ether;
            try
            {
                // Get signature counts for all masternodes in the completed epoch
                var signatureCounts = signatureTracker.GetSignatureCounts(epochStartBlock, epochEndBlock);

                if (signatureCounts == null || signatureCounts.Count == 0)
                {
                    logger.Warn($"No signature data available for epoch {completedEpoch}");
                    return Array.Empty<BlockReward>();
                }

                // Calculate total signatures across all masternodes
                long totalSignatures = signatureCounts.Values.Sum();

                if (totalSignatures == 0)
                {
                    logger.Warn($"Total signatures for epoch {completedEpoch} is zero");
                    return Array.Empty<BlockReward>();
                }

                // Calculate and distribute rewards
                var rewards = new List<BlockReward>();

                foreach (var masternodeSignature in signatureCounts)
                {
                    Address masternodeAddress = masternodeSignature.Key;
                    long signatureCount = masternodeSignature.Value;

                    // Calculate this masternode's proportional share of the epoch reward
                    // Formula: (masternode_signatures / total_signatures) * EPOCH_REWARD
                    UInt256 masternodeBaseReward = CalculateProportionalReward(
                        signatureCount,
                        totalSignatures,
                        EPOCH_REWARD
                    );

                    // Split the reward according to XDPoS distribution model
                    var (infraReward, stakingPool, foundationReward) = SplitReward(masternodeBaseReward);

                    // 1. Infrastructure reward (40%) goes directly to masternode operator
                    rewards.Add(new BlockReward(masternodeAddress, infraReward, BlockRewardType.Block));

                    // 2. Foundation reward (10%) goes to XDC Foundation
                    rewards.Add(new BlockReward(spec.FoundationWallet, foundationReward, BlockRewardType.External));

                    // 3. Staking reward (50%) is distributed among delegators proportionally
                    var delegatorRewards = DistributeStakingRewards(
                        masternodeAddress,
                        stakingPool,
                        block.Number
                    );

                    rewards.AddRange(delegatorRewards);
                }

                return rewards.ToArray();
            }
            catch (Exception ex)
            {
                logger.Error($"Error calculating XDPoS rewards for block {block.Number}", ex);
                throw;
            }
        }

        /// <summary>
        /// Calculates a proportional reward based on the number of signatures.
        /// Uses UInt256 arithmetic to maintain precision with large Wei values.
        /// 
        /// Formula: (signatureCount / totalSignatures) * totalReward
        /// </summary>
        private UInt256 CalculateProportionalReward(
            long signatureCount,
            long totalSignatures,
            UInt256 totalReward)
        {
            if (signatureCount <= 0 || totalSignatures <= 0)
            {
                return UInt256.Zero;
            }

            // Convert to UInt256 for precision
            UInt256 signatures = (UInt256)signatureCount;
            UInt256 total = (UInt256)totalSignatures;

            // Calculate: (signatures * totalReward) / total
            // Order of operations matters to maintain precision
            UInt256 numerator = signatures * totalReward;
            UInt256 reward = numerator / total;

            return reward;
        }

        /// <summary>
        /// Splits a masternode's base reward according to the XDPoS distribution model:
        /// - 40% Infrastructure (masternode operator)
        /// - 50% Staking (delegators)
        /// - 10% Foundation (XDC Foundation)
        /// </summary>
        private (UInt256 infrastructure, UInt256 staking, UInt256 foundation) SplitReward(UInt256 baseReward)
        {
            // Calculate each component
            // Using integer division with appropriate scaling to avoid precision loss

            // 40% for infrastructure
            UInt256 infrastructure = (baseReward * 40) / 100;

            // 50% for staking pool
            UInt256 staking = (baseReward * 50) / 100;

            // 10% for foundation
            UInt256 foundation = (baseReward * 10) / 100;

            // Handle any rounding remainder by adding it to infrastructure
            UInt256 allocated = infrastructure + staking + foundation;
            if (allocated < baseReward)
            {
                infrastructure += (baseReward - allocated);
            }

            return (infrastructure, staking, foundation);
        }

        /// <summary>
        /// Distributes the staking reward pool among all delegators who have staked with this masternode.
        /// Each delegator receives a proportion based on their stake amount relative to the total stake.
        /// 
        /// Only delegators who were active at the checkpoint block receive rewards.
        /// </summary>
        private List<BlockReward> DistributeStakingRewards(
            Address masternodeAddress,
            UInt256 stakingPool,
            long checkpointBlock)
        {
            var delegatorRewards = new List<BlockReward>();

            if (stakingPool.IsZero)
            {
                return delegatorRewards;
            }

            // Get all active stakes for this masternode at the checkpoint
            var stakes = stakingManager.GetActiveStakes(masternodeAddress, checkpointBlock);

            if (stakes == null || stakes.Count == 0)
            {
                // No delegators, staking pool could be reallocated to infrastructure
                // or left undistributed depending on implementation choice
                if (logger.IsDebug)
                {
                    logger.Debug($"No active delegators for masternode {masternodeAddress}, staking pool: {stakingPool} Wei");
                }
                return delegatorRewards;
            }

            // Calculate total stake across all delegators
            UInt256 totalStake = UInt256.Zero;
            foreach (var stake in stakes)
            {
                totalStake += stake.Amount;
            }

            if (totalStake.IsZero)
            {
                logger.Warn($"Total stake is zero for masternode {masternodeAddress}");
                return delegatorRewards;
            }

            // Distribute rewards proportionally to each delegator
            foreach (var stake in stakes)
            {
                // Formula: (delegator_stake / total_stake) * staking_pool
                UInt256 delegatorReward = (stake.Amount * stakingPool) / totalStake;

                if (!delegatorReward.IsZero)
                {
                    delegatorRewards.Add(new BlockReward(
                        stake.DelegatorAddress,
                        delegatorReward,
                        BlockRewardType.External
                    ));
                }
            }

            return delegatorRewards;
        }

        public UInt256 RewardInflation(IXdcReleaseSpec spec, UInt256 chainReward, long number, long blockPerYear)
        {
            UInt256 reward = chainReward;
            if (blockPerYear * 2 <= number && number < blockPerYear * 5)
            {
                reward = chainReward / 2;
            }
            if (blockPerYear * 5 <= number)
            {
                reward = chainReward / 4;
            }
            return reward;
        }
    }

    public class StakePosition
    {
        public Address DelegatorAddress { get; set; }
        public UInt256 Amount { get; set; }
        public long StakedAtBlock { get; set; }

        public StakePosition(Address delegator, UInt256 amount, long stakedAt)
        {
            DelegatorAddress = delegator;
            Amount = amount;
            StakedAtBlock = stakedAt;
        }
    }

    public interface IXdcSignatureTracker
    {
        /// <summary>
        /// Gets the signature count for each masternode between the specified block range.
        /// </summary>
        /// <param name="startBlock">Start of the range (inclusive)</param>
        /// <param name="endBlock">End of the range (inclusive)</param>
        /// <returns>Dictionary mapping masternode address to signature count</returns>
        Dictionary<Address, long> GetSignatureCounts(long startBlock, long endBlock);
    }

    public interface IXdcStakingManager
    {
        /// <summary>
        /// Gets all active stake positions for a masternode at a specific block.
        /// Only returns stakes that were active (not withdrawn) at the checkpoint.
        /// </summary>
        /// <param name="masternodeAddress">The masternode address</param>
        /// <param name="atBlock">The block number to check stakes at</param>
        /// <returns>List of active stake positions</returns>
        List<StakePosition> GetActiveStakes(Address masternodeAddress, long atBlock);
    }
}
