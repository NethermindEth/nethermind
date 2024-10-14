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

    // TODO: write converter
    public IDictionary<string, long> DifficultyBombDelays { get; set; }

    public string? SealEngineType => "Ethash";

    public void AddTransitions(SortedSet<long> blockNumbers, SortedSet<ulong> timestamps)
    {
        foreach (KeyValuePair<string, long> bombDelay in DifficultyBombDelays ?? Enumerable.Empty<KeyValuePair<string, long>>())
        {
            long key  = bombDelay.Key.StartsWith("0x") ?
                long.Parse(bombDelay.Key.AsSpan(2), NumberStyles.HexNumber) :
                long.Parse(bombDelay.Key);
            blockNumbers.Add(key);
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
        spec.IsEip7Enabled = spec.IsEip7Enabled || HomesteadTransition <= startBlock;
        spec.IsEip100Enabled = (Eip100bTransition ?? 0) <= startBlock;
        spec.DifficultyBoundDivisor = DifficultyBoundDivisor;
        spec.FixedDifficulty = FixedDifficulty;
    }

    private void SetDifficultyBombDelays(ReleaseSpec spec, long startBlock)
    {

        foreach (KeyValuePair<long, UInt256> blockReward in BlockReward ?? Enumerable.Empty<KeyValuePair<long, UInt256>>())
        {
            if (blockReward.Key <= startBlock)
            {
                spec.BlockReward = blockReward.Value;
            }
        }

        foreach (KeyValuePair<string, long> bombDelay in DifficultyBombDelays ?? Enumerable.Empty<KeyValuePair<string, long>>())
        {
            long key  = bombDelay.Key.StartsWith("0x") ?
                long.Parse(bombDelay.Key.AsSpan(2), NumberStyles.HexNumber) :
                long.Parse(bombDelay.Key);
            if (key <= startBlock)
            {
                spec.DifficultyBombDelay += bombDelay.Value;
            }
        }
    }



    public void ApplyToChainSpec(ChainSpec chainSpec)
    {
        IEnumerable<long?> difficultyBombDelaysBlockNumbers = DifficultyBombDelays
            .Keys.Select(key => key.StartsWith("0x") ? long.Parse(key.AsSpan(2), NumberStyles.HexNumber) : long.Parse(key))
            .Cast<long?>()
            .ToArray();

        chainSpec.MuirGlacierNumber = difficultyBombDelaysBlockNumbers?.Skip(2).FirstOrDefault();
        chainSpec.ArrowGlacierBlockNumber = difficultyBombDelaysBlockNumbers?.Skip(4).FirstOrDefault();
        chainSpec.GrayGlacierBlockNumber = difficultyBombDelaysBlockNumbers?.Skip(5).FirstOrDefault();
    }
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
                    UInt256Converter.Read(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan);
                var key = (long)property;
                reader.Read();
                if (reader.TokenType != JsonTokenType.String)
                {
                    throw new ArgumentException("Cannot deserialize BlockReward.");
                }

                var blockReward =
                    UInt256Converter.Read(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan);
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
