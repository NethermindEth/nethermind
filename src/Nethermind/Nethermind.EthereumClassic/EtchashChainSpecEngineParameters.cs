// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Specs;
using Nethermind.Consensus.Ethash;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.EthereumClassic;

/// <summary>
/// Chain spec engine parameters for Ethereum Classic (ETC) using the Etchash algorithm.
/// Inherits from EthashChainSpecEngineParameters but reimplements IChainSpecEngineParameters
/// to customize Fork ID calculation per ECIP-1082/ECIP-1091.
/// </summary>
public class EtchashChainSpecEngineParameters : EthashChainSpecEngineParameters, IChainSpecEngineParameters
{
    // Reimplemented to match "Etchash" engine section in chainspec
    string? IChainSpecEngineParameters.EngineName => "Etchash";

    // Keep using Ethash seal engine (same PoW validation logic)
    string? IChainSpecEngineParameters.SealEngineType => Core.SealEngineType.Ethash;

    /// <summary>
    /// Block number at which ECIP-1099 (Thanos/Etchash) activates.
    /// After this block, epoch length changes from 30000 to 60000.
    /// For ETC mainnet, this is block 11,700,000.
    /// </summary>
    public long? Ecip1099Transition { get; set; }

    /// <summary>
    /// Block number for EIP-3855 (PUSH0 opcode) - Spiral fork.
    /// ETC uses block-based transitions unlike post-merge ETH which uses timestamps.
    /// </summary>
    public long? Eip3855Transition { get; set; }

    /// <summary>
    /// Block number for EIP-3860 (initcode size limit) - Spiral fork.
    /// ETC uses block-based transitions unlike post-merge ETH which uses timestamps.
    /// </summary>
    public long? Eip3860Transition { get; set; }

    /// <summary>
    /// Block number for EIP-3651 (warm COINBASE) - Spiral fork.
    /// ETC uses block-based transitions unlike post-merge ETH which uses timestamps.
    /// </summary>
    public long? Eip3651Transition { get; set; }

    /// <summary>
    /// ECIP-1017 era length in blocks.
    /// ETC mainnet: 5,000,000. Mordor testnet: 2,000,000.
    /// </summary>
    public long Ecip1017EraRounds { get; set; }

    /// <summary>
    /// Block at which Die Hard activates (difficulty bomb paused at period 30).
    /// Null = bomb never existed (e.g., Mordor testnet).
    /// ETC mainnet: 3,000,000.
    /// </summary>
    public long? DieHardTransition { get; set; }

    /// <summary>
    /// Block at which Gotham activates (difficulty bomb delayed by 20 periods).
    /// Null = bomb never existed (e.g., Mordor testnet).
    /// ETC mainnet: 5,000,000.
    /// </summary>
    public long? GothamTransition { get; set; }

    /// <summary>
    /// Block at which ECIP-1041 activates (difficulty bomb removal).
    /// Null = bomb never existed (e.g., Mordor testnet).
    /// ETC mainnet: 5,900,000.
    /// </summary>
    public long? Ecip1041Transition { get; set; }

    /// <summary>
    /// Per ECIP-1082/ECIP-1091: Block reward reductions (ECIP-1017) should NOT affect Fork ID.
    /// This override excludes BlockReward entries from fork transitions.
    /// </summary>
    void IChainSpecEngineParameters.AddTransitions(SortedSet<long> blockNumbers, SortedSet<ulong> timestamps)
    {
        if (DifficultyBombDelays is not null)
        {
            foreach ((long blockNumber, _) in DifficultyBombDelays)
            {
                blockNumbers.Add(blockNumber);
            }
        }

        // BlockReward changes are intentionally NOT added to fork transitions per ECIP-1082

        blockNumbers.Add(HomesteadTransition);
        if (DaoHardforkTransition is not null) blockNumbers.Add(DaoHardforkTransition.Value);
        if (Eip100bTransition is not null) blockNumbers.Add(Eip100bTransition.Value);
        if (Ecip1099Transition is not null) blockNumbers.Add(Ecip1099Transition.Value);
        if (Eip3651Transition is not null) blockNumbers.Add(Eip3651Transition.Value);
        if (Eip3855Transition is not null) blockNumbers.Add(Eip3855Transition.Value);
        if (Eip3860Transition is not null) blockNumbers.Add(Eip3860Transition.Value);
    }

    void IChainSpecEngineParameters.ApplyToChainSpec(ChainSpec chainSpec)
    {
        // ETC doesn't have Muir Glacier, Arrow Glacier, or Gray Glacier
        chainSpec.MuirGlacierNumber = null;
        chainSpec.ArrowGlacierBlockNumber = null;
        chainSpec.GrayGlacierBlockNumber = null;
        chainSpec.HomesteadBlockNumber = HomesteadTransition;
        chainSpec.DaoForkBlockNumber = DaoHardforkTransition;
    }

    void IChainSpecEngineParameters.ApplyToReleaseSpec(Specs.ReleaseSpec spec, long startBlock, ulong? startTimestamp)
    {
        // Call base implementation first (sets BlockReward, etc.)
        base.ApplyToReleaseSpec(spec, startBlock, startTimestamp);

        // ETC difficulty bomb is handled by EtchashDifficultyCalculator with hardcoded logic:
        // - Die Hard (3M) to ECIP-1041 (5.9M): Bomb PAUSED at 2^28
        // - After ECIP-1041: Bomb REMOVED
        // The DifficultyBombDelays in chainspec are only used for Fork ID calculation,
        // not for actual difficulty computation. Clear them to avoid confusion.
        spec.DifficultyBombDelay = 0;

        // ETC Mystique does NOT include EIP-1559 (kept in chainspec for Fork ID only)
        spec.IsEip1559Enabled = false;
        spec.Eip1559TransitionBlock = long.MaxValue;

        // Spiral fork (block-based transitions for ETC)
        // EIP-3855: PUSH0 opcode
        if (Eip3855Transition is not null)
        {
            spec.IsEip3855Enabled = startBlock >= Eip3855Transition.Value;
        }

        // EIP-3860: Initcode size limit
        if (Eip3860Transition is not null)
        {
            spec.IsEip3860Enabled = startBlock >= Eip3860Transition.Value;
        }

        // EIP-3651: Warm COINBASE
        if (Eip3651Transition is not null)
        {
            spec.IsEip3651Enabled = startBlock >= Eip3651Transition.Value;
        }
    }
}
