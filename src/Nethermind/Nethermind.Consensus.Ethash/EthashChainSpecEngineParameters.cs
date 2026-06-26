// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.ChainSpecStyle.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Consensus.Ethash;

public class EthashChainSpecEngineParameters : IChainSpecEngineParameters
{
    public string EngineName => SealEngineType;
    public string SealEngineType => Core.SealEngineType.Ethash;

    public ulong HomesteadTransition { get; set; } = 0;
    public ulong? DaoHardforkTransition { get; set; }
    public Address? DaoHardforkBeneficiary { get; set; }
    public Address[] DaoHardforkAccounts { get; set; } = [];
    public ulong? Eip100bTransition { get; set; }
    public ulong? FixedDifficulty { get; set; }
    public ulong DifficultyBoundDivisor { get; set; } = 0x0800;
    public ulong DurationLimit { get; set; } = 13;
    public UInt256 MinimumDifficulty { get; set; } = 0;

    [JsonConverter(typeof(BlockRewardConverter))]
    public SortedDictionary<ulong, UInt256>? BlockReward { get; set; }
    [JsonConverter(typeof(DifficultyBombDelaysConverter))]
    public IDictionary<ulong, ulong>? DifficultyBombDelays { get; set; }

    public void AddTransitions(SortedSet<ulong> blockNumbers, SortedSet<ulong> timestamps)
    {
        if (DifficultyBombDelays is not null)
        {
            foreach ((ulong blockNumber, _) in DifficultyBombDelays)
            {
                blockNumbers.Add(blockNumber);
            }
        }

        if (BlockReward is not null)
        {
            foreach ((ulong blockNumber, _) in BlockReward)
            {
                blockNumbers.Add(blockNumber);
            }
        }

        blockNumbers.Add(HomesteadTransition);
        if (DaoHardforkTransition is not null) blockNumbers.Add(DaoHardforkTransition.Value);
        if (Eip100bTransition is not null) blockNumbers.Add(Eip100bTransition.Value);
    }

    public void ApplyToReleaseSpec(ReleaseSpec spec, ulong startBlock, ulong? startTimestamp)
    {
        SetDifficultyBombDelays(spec, startBlock);

        spec.IsEip2Enabled = HomesteadTransition <= startBlock;
        spec.IsEip7Enabled = HomesteadTransition <= startBlock;
        spec.IsEip100Enabled = (Eip100bTransition ?? 0) <= startBlock;
        spec.DifficultyBoundDivisor = DifficultyBoundDivisor;
        spec.FixedDifficulty = FixedDifficulty;
    }

    private void SetDifficultyBombDelays(ReleaseSpec spec, ulong startBlock)
    {
        if (BlockReward is not null)
        {
            foreach (KeyValuePair<ulong, UInt256> blockReward in BlockReward)
            {
                if (blockReward.Key <= startBlock)
                {
                    spec.BlockReward = blockReward.Value;
                }
            }
        }

        if (DifficultyBombDelays is not null)
        {
            foreach (KeyValuePair<ulong, ulong> bombDelay in DifficultyBombDelays)
            {
                if (bombDelay.Key <= startBlock)
                {
                    spec.DifficultyBombDelay += bombDelay.Value;
                }
            }
        }
    }

    public void ApplyToChainSpec(ChainSpec chainSpec)
    {
        chainSpec.MuirGlacierNumber = DifficultyBombDelays?.Keys.Skip(2).FirstOrDefault();
        chainSpec.ArrowGlacierBlockNumber = DifficultyBombDelays?.Keys.Skip(4).FirstOrDefault();
        chainSpec.GrayGlacierBlockNumber = DifficultyBombDelays?.Keys.Skip(5).FirstOrDefault();
        chainSpec.HomesteadBlockNumber = HomesteadTransition;
        chainSpec.DaoForkBlockNumber = DaoHardforkTransition;
    }
}
