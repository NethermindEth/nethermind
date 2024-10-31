// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.ChainSpecStyle.Json;

namespace Nethermind.Consensus.Ethash;

public class EthashChainSpecEngineParameters : IChainSpecEngineParameters
{
    public string? EngineName => SealEngineType;
    public string? SealEngineType => Core.SealEngineType.Ethash;

    public long HomesteadTransition { get; set; } = 0;
    public long? DaoHardforkTransition { get; set; }
    public Address DaoHardforkBeneficiary { get; set; }
    public Address[] DaoHardforkAccounts { get; set; } = [];
    public long? Eip100bTransition { get; set; }
    public long? FixedDifficulty { get; set; }
    public long DifficultyBoundDivisor { get; set; } = 0x0800;
    public long DurationLimit { get; set; } = 13;
    public UInt256 MinimumDifficulty { get; set; } = 0;

    [JsonConverter(typeof(LongUInt256DictionaryConverter))]
    public SortedDictionary<long, UInt256>? BlockReward { get; set; }

    [JsonConverter(typeof(DifficultyBombDelaysJsonConverter))]
    public IDictionary<long, long>? DifficultyBombDelays { get; set; }

    public void AddTransitions(SortedSet<long> blockNumbers, SortedSet<ulong> timestamps)
    {
        if (DifficultyBombDelays is not null)
        {
            foreach ((long blockNumber, _) in DifficultyBombDelays)
            {
                blockNumbers.Add(blockNumber);
            }
        }

        if (BlockReward is not null)
        {
            foreach ((long blockNumber, _) in BlockReward)
            {
                blockNumbers.Add(blockNumber);
            }
        }

        blockNumbers.Add(HomesteadTransition);
        if (DaoHardforkTransition is not null) blockNumbers.Add(DaoHardforkTransition.Value);
        if (Eip100bTransition is not null) blockNumbers.Add(Eip100bTransition.Value);
    }

    public void ApplyToReleaseSpec(ReleaseSpec spec, long startBlock, ulong? startTimestamp)
    {
        SetDifficultyBombDelays(spec, startBlock);

        spec.IsEip2Enabled = HomesteadTransition <= startBlock;
        spec.IsEip7Enabled = HomesteadTransition <= startBlock;
        spec.IsEip100Enabled = (Eip100bTransition ?? 0) <= startBlock;
        spec.DifficultyBoundDivisor = DifficultyBoundDivisor;
        spec.FixedDifficulty = FixedDifficulty;
    }

    private void SetDifficultyBombDelays(ReleaseSpec spec, long startBlock)
    {
        if (BlockReward is not null)
        {
            foreach (KeyValuePair<long, UInt256> blockReward in BlockReward)
            {
                if (blockReward.Key <= startBlock)
                {
                    spec.BlockReward = blockReward.Value;
                }
            }
        }

        if (DifficultyBombDelays is not null)
        {
            foreach (KeyValuePair<long, long> bombDelay in DifficultyBombDelays)
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

    private class DifficultyBombDelaysJsonConverter : JsonConverter<IDictionary<long, long>>
    {
        public override void Write(Utf8JsonWriter writer, IDictionary<long, long> value, JsonSerializerOptions options)
        {
            throw new NotSupportedException();
        }

        public override IDictionary<long, long> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = new Dictionary<long, long>();
            if (reader.TokenType == JsonTokenType.String)
            {
                value.Add(0, JsonSerializer.Deserialize<long>(ref reader, options));
            }
            else if (reader.TokenType == JsonTokenType.Number)
            {
                value.Add(0, reader.GetInt64());
            }
            else if (reader.TokenType == JsonTokenType.StartObject)
            {
                reader.Read();
                while (reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName)
                    {
                        throw new ArgumentException("Cannot deserialize DifficultyBombDelays.");
                    }
                    string keyString = reader.GetString();
                    var key = keyString.StartsWith("0x") ? Convert.ToInt64(keyString, 16) : long.Parse(keyString);
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        string valueString = reader.GetString();
                        value.Add(key, valueString.StartsWith("0x") ? Convert.ToInt64(valueString, 16) : long.Parse(valueString));
                    }
                    else if (reader.TokenType == JsonTokenType.Number)
                    {
                        value.Add(key, reader.GetInt64());
                    }
                    else
                    {
                        throw new ArgumentException("Cannot deserialize DifficultyBombDelays.");
                    }

                    reader.Read();
                }
            }
            else
            {
                throw new ArgumentException("Cannot deserialize DifficultyBombDelays.");
            }

            return value;
        }
    }

}
