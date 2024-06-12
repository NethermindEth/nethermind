// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Specs.ChainSpecStyle.Json
{
    [SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
    [SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    internal class ChainSpecJson
    {
        public string Name { get; set; }
        public string DataDir { get; set; }
        public EngineJson Engine { get; set; }
        public ChainSpecParamsJson Params { get; set; }
        [JsonPropertyName("genesis")]
        public ChainSpecGenesisJson Genesis { get; set; }
        public string[] Nodes { get; set; }
        [JsonPropertyName("accounts")]
        public Dictionary<string, AllocationJson> Accounts { get; set; }

        internal class EthashEngineJson
        {
            public long? HomesteadTransition => Params?.HomesteadTransition;
            public long? DaoHardforkTransition => Params?.DaoHardforkTransition;
            public Address DaoHardforkBeneficiary => Params?.DaoHardforkBeneficiary;
            public Address[] DaoHardforkAccounts => Params?.DaoHardforkAccounts;
            public long? Eip100bTransition => Params?.Eip100bTransition;
            public long? FixedDifficulty => Params?.FixedDifficulty;
            public long? DifficultyBoundDivisor => Params?.DifficultyBoundDivisor;
            public long? DurationLimit => Params?.DurationLimit;
            public UInt256? MinimumDifficulty => Params?.MinimumDifficulty;
            public IDictionary<long, UInt256> BlockReward => Params?.BlockReward;
            public IDictionary<string, long> DifficultyBombDelays => Params?.DifficultyBombDelays;
            public EthashEngineParamsJson Params { get; set; }
        }

        internal class EthashEngineParamsJson
        {
            public UInt256? MinimumDifficulty { get; set; }
            public long? DifficultyBoundDivisor { get; set; }
            public long? DurationLimit { get; set; }
            public long HomesteadTransition { get; set; }
            public long? DaoHardforkTransition { get; set; }
            public Address DaoHardforkBeneficiary { get; set; }
            public Address[] DaoHardforkAccounts { get; set; }
            public long Eip100bTransition { get; set; }
            public long? FixedDifficulty { get; set; }
            public BlockRewardJson BlockReward { get; set; }
            public Dictionary<string, long> DifficultyBombDelays { get; set; }
        }

        internal class CliqueEngineJson
        {
            public ulong Period => Params.Period;
            public ulong Epoch => Params.Epoch;
            public UInt256? BlockReward => Params.BlockReward;
            public CliqueEngineParamsJson Params { get; set; }
        }

        internal class CliqueEngineParamsJson
        {
            public ulong Period { get; set; }
            public ulong Epoch { get; set; }
            public UInt256? BlockReward { get; set; }
        }

        internal class AuraEngineParamsJson
        {
            public StepDurationJson StepDuration { get; set; }
            public BlockRewardJson BlockReward { get; set; }
            public long MaximumUncleCountTransition { get; set; }
            public long? MaximumUncleCount { get; set; }
            public Address BlockRewardContractAddress { get; set; }
            public long? BlockRewardContractTransition { get; set; }
            public IDictionary<long, Address> BlockRewardContractTransitions { get; set; } = new Dictionary<long, Address>();
            public long ValidateScoreTransition { get; set; }
            public long ValidateStepTransition { get; set; }
            public AuRaValidatorJson Validators { get; set; }
            public IDictionary<long, Address> RandomnessContractAddress { get; set; } = new Dictionary<long, Address>();
            public IDictionary<long, Address> BlockGasLimitContractTransitions { get; set; } = new Dictionary<long, Address>();
            public long? TwoThirdsMajorityTransition { get; set; }
            public long? PosdaoTransition { get; set; }
            public IDictionary<long, IDictionary<Address, byte[]>> RewriteBytecode { get; set; } = new Dictionary<long, IDictionary<Address, byte[]>>();
            public Address WithdrawalContractAddress { get; set; }

            [JsonConverter(typeof(StepDurationJsonConverter))]
            public class StepDurationJson : SortedDictionary<long, long> { }
        }

        [JsonConverter(typeof(BlockRewardJsonConverter))]
        public class BlockRewardJson : SortedDictionary<long, UInt256> { }

        internal class AuRaValidatorJson
        {
            public Address[] List { get; set; }
            public Address Contract { get; set; }
            public Address SafeContract { get; set; }
            public Dictionary<long, AuRaValidatorJson> Multi { get; set; }

            public AuRaParameters.ValidatorType GetValidatorType()
            {
                if (List is not null)
                {
                    return AuRaParameters.ValidatorType.List;
                }
                else if (Contract is not null)
                {
                    return AuRaParameters.ValidatorType.ReportingContract;
                }
                else if (SafeContract is not null)
                {
                    return AuRaParameters.ValidatorType.Contract;
                }
                else if (Multi is not null)
                {
                    return AuRaParameters.ValidatorType.Multi;
                }
                else
                {
                    throw new NotSupportedException("AuRa validator type not supported.");
                }
            }
        }

        internal class AuraEngineJson
        {
            public IDictionary<long, long> StepDuration => Params.StepDuration;

            public IDictionary<long, UInt256> BlockReward => Params.BlockReward;

            public long MaximumUncleCountTransition => Params.MaximumUncleCountTransition;

            public long? MaximumUncleCount => Params.MaximumUncleCount;

            public Address BlockRewardContractAddress => Params.BlockRewardContractAddress;

            public long? BlockRewardContractTransition => Params.BlockRewardContractTransition;

            public IDictionary<long, Address> BlockRewardContractTransitions => Params.BlockRewardContractTransitions;

            public long ValidateScoreTransition => Params.ValidateScoreTransition;

            public long ValidateStepTransition => Params.ValidateStepTransition;

            public long? PosdaoTransition => Params.PosdaoTransition;

            public long? TwoThirdsMajorityTransition => Params.TwoThirdsMajorityTransition;

            public AuRaValidatorJson Validator => Params.Validators;

            public IDictionary<long, Address> RandomnessContractAddress => Params.RandomnessContractAddress;

            public IDictionary<long, Address> BlockGasLimitContractTransitions => Params.BlockGasLimitContractTransitions;

            public IDictionary<long, IDictionary<Address, byte[]>> RewriteBytecode => Params.RewriteBytecode;

            public Address WithdrawalContractAddress => Params.WithdrawalContractAddress;

            public AuraEngineParamsJson Params { get; set; }
        }

        internal class OptimismEngineJson
        {
            public ulong RegolithTimestamp => Params.RegolithTimestamp;
            public long BedrockBlockNumber => Params.BedrockBlockNumber;
            public ulong? CanyonTimestamp => Params.CanyonTimestamp;
            public ulong? EcotoneTimestamp => Params.EcotoneTimestamp;
            public ulong? FjordTimestamp => Params.FjordTimestamp;
            public Address L1FeeRecipient => Params.L1FeeRecipient;
            public Address L1BlockAddress => Params.L1BlockAddress;
            public UInt256 CanyonBaseFeeChangeDenominator => Params.CanyonBaseFeeChangeDenominator;
            public Address Create2DeployerAddress => Params.Create2DeployerAddress;
            public byte[] Create2DeployerCode => Params.Create2DeployerCode;
            public OptimismEngineParamsJson Params { get; set; }
        }

        internal class OptimismEngineParamsJson
        {
            public ulong RegolithTimestamp { get; set; }
            public long BedrockBlockNumber { get; set; }
            public ulong? CanyonTimestamp { get; set; }
            public ulong? EcotoneTimestamp { get; set; }
            public ulong? FjordTimestamp { get; set; }
            public Address L1FeeRecipient { get; set; }
            public Address L1BlockAddress { get; set; }
            public UInt256 CanyonBaseFeeChangeDenominator { get; set; }
            public Address Create2DeployerAddress { get; set; }
            public byte[] Create2DeployerCode { get; set; }
        }

        internal class NethDevJson
        {
        }

        internal class EngineJson
        {
            public EthashEngineJson Ethash { get; set; }
            public CliqueEngineJson Clique { get; set; }
            public AuraEngineJson AuthorityRound { get; set; }
            public OptimismEngineJson Optimism { get; set; }
            public NethDevJson NethDev { get; set; }

            [JsonExtensionData]
            public Dictionary<string, JsonElement> CustomEngineData { get; set; }
        }
    }
}
