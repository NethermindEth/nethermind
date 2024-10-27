// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.Ethash;

public class EthashChainSpecEngineParameters : IChainSpecEngineParameters
{
    public string? EngineName => "Ethash";
    public string? SealEngineType => "Ethash";

    public long HomesteadTransition { get; set; } = 0;
    public long? DaoHardforkTransition { get; set; }
    public Address DaoHardforkBeneficiary { get; set; }
    public Address[] DaoHardforkAccounts { get; set; } = Array.Empty<Address>();
    public long? Eip100bTransition { get; set; }
    public long? FixedDifficulty { get; set; }
    public long DifficultyBoundDivisor { get; set; } = 0x0800;
    public long DurationLimit { get; set; } = 13;
    public UInt256 MinimumDifficulty { get; set; } = 0;

    [JsonConverter(typeof(BlockRewardJsonConverter))]
    public SortedDictionary<long, UInt256> BlockReward { get; set; }

    [JsonConverter(typeof(DifficultyBombDelaysJsonConverter))]
    public IDictionary<long, long>? DifficultyBombDelays { get; set; }

    public void AddTransitions(SortedSet<long> blockNumbers, SortedSet<ulong> timestamps)
    {
        foreach (KeyValuePair<long, long> bombDelay in DifficultyBombDelays ??
                                                       Enumerable.Empty<KeyValuePair<long, long>>())
        {
            blockNumbers.Add(bombDelay.Key);
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
        foreach (KeyValuePair<long, UInt256> blockReward in BlockReward ??
                                                            Enumerable.Empty<KeyValuePair<long, UInt256>>())
        {
            if (blockReward.Key <= startBlock)
            {
                spec.BlockReward = blockReward.Value;
            }
        }

        foreach (KeyValuePair<long, long> bombDelay in DifficultyBombDelays ??
                                                       Enumerable.Empty<KeyValuePair<long, long>>())
        {
            if (bombDelay.Key <= startBlock)
            {
                spec.DifficultyBombDelay += bombDelay.Value;
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

    internal class BlockRewardJsonConverter : JsonConverter<SortedDictionary<long, UInt256>>
    {
        public override void Write(Utf8JsonWriter writer, SortedDictionary<long, UInt256> value,
            JsonSerializerOptions options)
        {
            throw new NotSupportedException();
        }

        public override SortedDictionary<long, UInt256> Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options)
        {
            var value = new SortedDictionary<long, UInt256>();
            if (reader.TokenType == JsonTokenType.String)
            {
                var blockReward = JsonSerializer.Deserialize<UInt256>(ref reader, options);
                value.Add(0, blockReward);
            }
            else if (reader.TokenType == JsonTokenType.Number)
            {
                value.Add(0, new UInt256(reader.GetUInt64()));
            }
            else if (reader.TokenType == JsonTokenType.StartObject)
            {
                reader.Read();
                while (reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName)
                    {
                        throw new ArgumentException("Cannot deserialize BlockReward.");
                    }

                    var property =
                        UInt256Converter.Read(reader.HasValueSequence
                            ? reader.ValueSequence.ToArray()
                            : reader.ValueSpan);
                    var key = (long)property;
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.String)
                    {
                        throw new ArgumentException("Cannot deserialize BlockReward.");
                    }

                    var blockReward =
                        UInt256Converter.Read(reader.HasValueSequence
                            ? reader.ValueSequence.ToArray()
                            : reader.ValueSpan);
                    value.Add(key, blockReward);

                    reader.Read();
                }
            }
            else
            {
                throw new ArgumentException("Cannot deserialize BlockReward.");
            }

            return value;
        }
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
