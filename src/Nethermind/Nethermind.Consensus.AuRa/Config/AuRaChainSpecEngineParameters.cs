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
using Nethermind.Core.Collections;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.ChainSpecStyle.Json;

namespace Nethermind.Consensus.AuRa.Config;

public class AuRaChainSpecEngineParameters : IChainSpecEngineParameters
{
    public const ulong TransitionDisabled = ulong.MaxValue;
    public string? EngineName => "AuthorityRound";
    public string? SealEngineType => Core.SealEngineType.AuRa;

    [JsonConverter(typeof(StepDurationJsonConverter))]
    public SortedDictionary<ulong, long> StepDuration { get; set; } = [];

    [JsonConverter(typeof(BlockRewardConverter))]
    public SortedDictionary<ulong, UInt256>? BlockReward { get; set; }

    public ulong? MaximumUncleCountTransition { get; set; }

    public ulong? MaximumUncleCount { get; set; }

    public Address? BlockRewardContractAddress { get; set; }

    public ulong? BlockRewardContractTransition { get; set; }

    public IDictionary<ulong, Address> BlockRewardContractTransitions { get; set; } = new Dictionary<ulong, Address>();

    public ulong ValidateScoreTransition { get; set; }

    public ulong ValidateStepTransition { get; set; }

    [JsonPropertyName("Validators")]
    public AuRaValidatorJson ValidatorsJson { get; set; }

    public IDictionary<ulong, Address> RandomnessContractAddress { get; set; } = new Dictionary<ulong, Address>();

    public IDictionary<ulong, Address> BlockGasLimitContractTransitions { get; set; } = new Dictionary<ulong, Address>();

    public ulong TwoThirdsMajorityTransition { get; set; } = TransitionDisabled;

    public ulong PosdaoTransition { get; set; } = TransitionDisabled;

    public IDictionary<ulong, IDictionary<Address, byte[]>> RewriteBytecode { get; set; } = new Dictionary<ulong, IDictionary<Address, byte[]>>();
    public IDictionary<ulong, IDictionary<Address, byte[]>> RewriteBytecodeTimestamp { get; set; } = new Dictionary<ulong, IDictionary<Address, byte[]>>();

    public IEnumerable<(ulong, Address, byte[])> RewriteBytecodeTimestampParsed
    {
        get
        {
            foreach (KeyValuePair<ulong, IDictionary<Address, byte[]>> timestampOverrides in RewriteBytecodeTimestamp)
            {
                foreach (KeyValuePair<Address, byte[]> addressOverride in timestampOverrides.Value)
                {
                    yield return (timestampOverrides.Key, addressOverride.Key, addressOverride.Value);
                }
            }
        }
    }

    public Address WithdrawalContractAddress { get; set; }

    private AuRaParameters.Validator? _validators;

    [JsonIgnore]
    public AuRaParameters.Validator Validators
    {
        get => _validators ??= LoadValidator(ValidatorsJson);
    }

    public void ApplyToReleaseSpec(ReleaseSpec spec, ulong startBlock, ulong? startTimestamp)
    {
        spec.MaximumUncleCount = (int)(startBlock >= (MaximumUncleCountTransition ?? ulong.MaxValue) ? MaximumUncleCount ?? 2 : 2);
        spec.Eip158IgnoredAccount = Address.SystemUser;
    }

    public void AddTransitions(SortedSet<ulong> blockNumbers, SortedSet<ulong> timestamps) => timestamps.AddRange(RewriteBytecodeTimestamp.Keys);

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
                throw new ArgumentOutOfRangeException(nameof(validatorJson), validatorType, "Unknown validator type.");
        }

        return validator;
    }

    private class StepDurationJsonConverter : JsonConverter<SortedDictionary<ulong, long>>
    {
        public override void Write(Utf8JsonWriter writer, SortedDictionary<ulong, long> value, JsonSerializerOptions options) => throw new NotSupportedException();

        public override SortedDictionary<ulong, long> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            SortedDictionary<ulong, long> value = [];
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
                    ulong key = ulong.Parse(reader.GetString());
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
        public Dictionary<ulong, AuRaValidatorJson> Multi { get; set; } = [];

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
