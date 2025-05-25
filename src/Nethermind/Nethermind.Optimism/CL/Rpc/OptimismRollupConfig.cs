// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Optimism.CL;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Optimism.Cl.Rpc;

/// <remarks>
/// See: https://github.com/ethereum-optimism/optimism/blob/c8b9f62736a7dad7e569719a84c406605f4472e6/op-node/rollup/types.go#L67
/// </remarks>
public sealed record OptimismRollupConfig
{
    [JsonPropertyName("genesis")]
    public required OptimismGenesis Genesis { get; init; }
    [JsonPropertyName("block_time")]
    public required ulong BlockTime { get; init; }
    [JsonPropertyName("max_sequencer_drift")]
    public required ulong MaxSequencerDrift { get; init; }
    [JsonPropertyName("seq_window_size")]
    public required ulong SeqWindowSize { get; init; }
    [JsonPropertyName("channel_timeout")]
    public required ulong ChannelTimeout { get; init; }
    [JsonPropertyName("l1_chain_id")]
    public required ulong L1ChainID { get; init; }
    [JsonPropertyName("l2_chain_id")]
    public required ulong L2ChainID { get; init; }

    [JsonPropertyName("regolith_time")]
    public required ulong? RegolithTime { get; init; }
    [JsonPropertyName("canyon_time")]
    public required ulong? CanyonTime { get; init; }
    [JsonPropertyName("delta_time")]
    public required ulong? DeltaTime { get; init; }
    [JsonPropertyName("ecotone_time")]
    public required ulong? EcotoneTime { get; init; }
    [JsonPropertyName("fjord_time")]
    public required ulong? FjordTime { get; init; }
    [JsonPropertyName("granite_time")]
    public required ulong? GraniteTime { get; init; }
    [JsonPropertyName("holocene_time")]
    public required ulong? HoloceneTime { get; init; }
    [JsonPropertyName("isthmus_time")]
    public required ulong? IsthmusTime { get; init; }
    [JsonPropertyName("batch_inbox_address")]
    public required Address BatchInboxAddress { get; init; }
    [JsonPropertyName("deposit_contract_address")]
    public required Address DepositContractAddress { get; init; }
    [JsonPropertyName("l1_system_config_address")]
    public required Address L1SystemConfigAddress { get; init; }
    [JsonPropertyName("chain_op_config")]
    public required OptimismChainConfig ChainOpConfig { get; init; }

    public sealed record OptimismGenesis
    {
        [JsonPropertyName("l1")]
        public required BlockId L1 { get; init; }
        [JsonPropertyName("l2")]
        public required BlockId L2 { get; init; }
        [JsonPropertyName("l2_time")]
        public required ulong L2Time { get; init; }
        [JsonPropertyName("system_config")]
        public required OptimismSystemConfig SystemConfig { get; init; }
    }

    public sealed record OptimismChainConfig
    {
        [JsonPropertyName("eip1559Elasticity")]
        public required ulong EIP1559Elasticity { get; init; }
        [JsonPropertyName("eip1559Denominator")]
        public required ulong EIP1559Denominator { get; init; }
        [JsonPropertyName("eip1559DenominatorCanyon")]
        public required ulong? EIP1559DenominatorCanyon { get; init; }
    }

    public static OptimismRollupConfig Build(
        CLChainSpecEngineParameters clParameters,
        OptimismChainSpecEngineParameters engineParameters,
        ChainSpec chainSpec)
    {
        return new OptimismRollupConfig
        {
            Genesis = new OptimismGenesis
            {
                L1 = new BlockId { Number = clParameters.L1ChainId!.Value, Hash = clParameters.L1GenesisHash! },
                L2 = new BlockId { Number = (ulong)chainSpec.Genesis.Number, Hash = chainSpec.Genesis.GetOrCalculateHash() },
                L2Time = chainSpec.Genesis.Timestamp,
                SystemConfig = clParameters.GenesisSystemConfig!
            },
            BlockTime = clParameters.L2BlockTime!.Value,
            MaxSequencerDrift = clParameters.MaxSequencerDrift!.Value,
            SeqWindowSize = clParameters.SeqWindowSize!.Value,
            ChannelTimeout = clParameters.ChannelTimeoutBedrock!.Value,
            L1ChainID = clParameters.L1ChainId!.Value,
            L2ChainID = chainSpec.ChainId,

            RegolithTime = engineParameters.RegolithTimestamp,
            CanyonTime = engineParameters.CanyonTimestamp,
            DeltaTime = engineParameters.DeltaTimestamp,
            EcotoneTime = engineParameters.EcotoneTimestamp,
            FjordTime = engineParameters.FjordTimestamp,
            GraniteTime = engineParameters.GraniteTimestamp,
            HoloceneTime = engineParameters.HoloceneTimestamp,
            IsthmusTime = engineParameters.IsthmusTimestamp,

            BatchInboxAddress = clParameters.BatchSubmitter!,
            DepositContractAddress = chainSpec.Parameters.DepositContractAddress,
            L1SystemConfigAddress = clParameters.SystemConfigProxy!,

            ChainOpConfig = new OptimismChainConfig
            {
                EIP1559Elasticity = (ulong)chainSpec.Parameters.Eip1559ElasticityMultiplier!.Value,
                EIP1559Denominator = (ulong)chainSpec.Parameters.Eip1559BaseFeeMaxChangeDenominator!.Value,
                EIP1559DenominatorCanyon = (ulong)engineParameters.CanyonBaseFeeChangeDenominator!.Value
            }
        };
    }
}
