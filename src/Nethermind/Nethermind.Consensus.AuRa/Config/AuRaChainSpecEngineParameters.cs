// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.ChainSpecStyle.Json;

namespace Nethermind.Consensus.AuRa.Config;

public class AuRaChainSpecEngineParameters : IChainSpecEngineParameters
{
    public const long TransitionDisabled = long.MaxValue;
    public string? EngineName => "AuthorityRound";
    public string? SealEngineType => Core.SealEngineType.AuRa;

    [JsonConverter(typeof(StepDurationJsonConverter))]
    public SortedDictionary<long, long> StepDuration { get; set; } = new();

    [JsonConverter(typeof(BlockRewardConverter))]
    public SortedDictionary<long, UInt256>? BlockReward { get; set; }

    public long? MaximumUncleCountTransition { get; set; }

    public long? MaximumUncleCount { get; set; }

    public Address? BlockRewardContractAddress { get; set; }

    public long? BlockRewardContractTransition { get; set; }

    public IDictionary<long, Address> BlockRewardContractTransitions { get; set; } = new Dictionary<long, Address>();

    public long ValidateScoreTransition { get; set; }

    public long ValidateStepTransition { get; set; }

    [JsonPropertyName("Validators")]
    public AuRaValidatorJson ValidatorsJson { get; set; }

    public IDictionary<long, Address> RandomnessContractAddress { get; set; } = new Dictionary<long, Address>();

    public IDictionary<long, Address> BlockGasLimitContractTransitions { get; set; } = new Dictionary<long, Address>();

    public long TwoThirdsMajorityTransition { get; set; } = TransitionDisabled;

    public long PosdaoTransition { get; set; } = TransitionDisabled;

    public IDictionary<long, IDictionary<Address, byte[]>> RewriteBytecode { get; set; } = new Dictionary<long, IDictionary<Address, byte[]>>();

    public Address WithdrawalContractAddress { get; set; }

    private AuRaParameters.Validator? _validators;

    [JsonIgnore]
    public AuRaParameters.Validator Validators
    {
        get => _validators ??= LoadValidator(ValidatorsJson);
    }

    public void ApplyToReleaseSpec(ReleaseSpec spec, long startBlock, ulong? startTimestamp)
    {
        spec.MaximumUncleCount = (int)(startBlock >= (MaximumUncleCountTransition ?? long.MaxValue) ? MaximumUncleCount ?? 2 : 2);
    }

    static AuRaParameters.Validator LoadValidator(AuRaValidatorJson validatorJson, int level = 0)
    {
        AuRaParameters.ValidatorType validatorType = validatorJson.GetValidatorType();
        AuRaParameters.Validator validator = new() { ValidatorType = validatorType };
        switch (validator.ValidatorType)
        {
            case AuRaParameters.ValidatorType.List:
                validator.Addresses = validatorJson.List;
                break;
            case AuRaParameters.ValidatorType.Contract:
                validator.Addresses = [validatorJson.SafeContract];
                break;
            case AuRaParameters.ValidatorType.ReportingContract:
                validator.Addresses = [validatorJson.Contract];
                break;
            case AuRaParameters.ValidatorType.Multi:
                if (level != 0) throw new ArgumentException("AuRa multi validator cannot be inner validator.");
                validator.Validators = validatorJson.Multi
                    .ToDictionary(kvp => kvp.Key, kvp => LoadValidator(kvp.Value, level + 1))
                    .ToImmutableSortedDictionary();
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        return validator;
    }

    private class StepDurationJsonConverter : JsonConverter<SortedDictionary<long, long>>
    {
        public override void Write(Utf8JsonWriter writer, SortedDictionary<long, long> value, JsonSerializerOptions options)
        {
            throw new NotSupportedException();
        }

        public override SortedDictionary<long, long> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = new SortedDictionary<long, long>();
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
                        throw new ArgumentException("Cannot deserialize BlockReward.");
                    }
                    var key = long.Parse(reader.GetString());
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        value.Add(key, long.Parse(reader.GetString()));
                    }
                    else if (reader.TokenType == JsonTokenType.Number)
                    {
                        value.Add(key, reader.GetInt64());
                    }
                    else
                    {
                        throw new ArgumentException("Cannot deserialize BlockReward.");
                    }

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

    public class AuRaValidatorJson
    {
        public Address[]? List { get; set; }
        public Address? Contract { get; set; }
        public Address? SafeContract { get; set; }
        public Dictionary<long, AuRaValidatorJson> Multi { get; set; } = new();

        public AuRaParameters.ValidatorType GetValidatorType()
        {
            if (List is not null)
            {
                return AuRaParameters.ValidatorType.List;
            }

            if (Contract is not null)
            {
                return AuRaParameters.ValidatorType.ReportingContract;
            }

            if (SafeContract is not null)
            {
                return AuRaParameters.ValidatorType.Contract;
            }

            if (Multi is not null)
            {
                return AuRaParameters.ValidatorType.Multi;
            }

            throw new NotSupportedException("AuRa validator type not supported.");
        }
    }
}
