// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
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
    [JsonPropertyName("jovian_time")]
    public required ulong? JovianTime { get; init; }
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
        Block genesis = chainSpec.Genesis ?? throw new ArgumentException("Chain spec genesis is missing.", nameof(chainSpec));
        OptimismSystemConfig systemConfig = clParameters.GenesisSystemConfig
            ?? throw new ArgumentException("Optimism genesis system config is missing.", nameof(clParameters));
        ulong l1ChainId = clParameters.L1ChainId
            ?? throw new ArgumentException("L1 chain id is missing.", nameof(clParameters));
        ulong l1GenesisNumber = clParameters.L1GenesisNumber
            ?? throw new ArgumentException("L1 genesis number is missing.", nameof(clParameters));
        Hash256 l1GenesisHash = clParameters.L1GenesisHash
            ?? throw new ArgumentException("L1 genesis hash is missing.", nameof(clParameters));
        ulong blockTime = clParameters.L2BlockTime
            ?? throw new ArgumentException("L2 block time is missing.", nameof(clParameters));
        ulong maxSequencerDrift = clParameters.MaxSequencerDrift
            ?? throw new ArgumentException("Maximum sequencer drift is missing.", nameof(clParameters));
        ulong sequenceWindowSize = clParameters.SeqWindowSize
            ?? throw new ArgumentException("Sequencing window size is missing.", nameof(clParameters));
        ulong channelTimeout = clParameters.ChannelTimeoutBedrock
            ?? throw new ArgumentException("Channel timeout is missing.", nameof(clParameters));
        Address batchInboxAddress = clParameters.BatcherInboxAddress
            ?? throw new ArgumentException("Batch inbox address is missing.", nameof(clParameters));
        Address depositContractAddress = clParameters.OptimismPortalProxy
            ?? throw new ArgumentException("Optimism portal proxy address is missing.", nameof(clParameters));
        Address systemConfigAddress = clParameters.SystemConfigProxy
            ?? throw new ArgumentException("System config proxy address is missing.", nameof(clParameters));
        ulong eip1559Elasticity = chainSpec.Parameters.Eip1559ElasticityMultiplier
            ?? throw new ArgumentException("EIP-1559 elasticity multiplier is missing.", nameof(chainSpec));
        UInt256 eip1559Denominator = chainSpec.Parameters.Eip1559BaseFeeMaxChangeDenominator
            ?? throw new ArgumentException("EIP-1559 base fee denominator is missing.", nameof(chainSpec));
        UInt256 canyonDenominator = engineParameters.CanyonBaseFeeChangeDenominator
            ?? throw new ArgumentException("Canyon base fee denominator is missing.", nameof(engineParameters));

        return new()
        {
            Genesis = new OptimismGenesis
            {
                L1 = new BlockId { Number = l1GenesisNumber, Hash = l1GenesisHash },
                L2 = new BlockId { Number = genesis.Number, Hash = genesis.GetOrCalculateHash() },
                L2Time = genesis.Timestamp,
                SystemConfig = systemConfig
            },
            BlockTime = blockTime,
            MaxSequencerDrift = maxSequencerDrift,
            SeqWindowSize = sequenceWindowSize,
            ChannelTimeout = channelTimeout,
            L1ChainID = l1ChainId,
            L2ChainID = chainSpec.ChainId,

            RegolithTime = engineParameters.RegolithTimestamp,
            CanyonTime = engineParameters.CanyonTimestamp,
            DeltaTime = engineParameters.DeltaTimestamp,
            EcotoneTime = engineParameters.EcotoneTimestamp,
            FjordTime = engineParameters.FjordTimestamp,
            GraniteTime = engineParameters.GraniteTimestamp,
            HoloceneTime = engineParameters.HoloceneTimestamp,
            IsthmusTime = engineParameters.IsthmusTimestamp,
            JovianTime = engineParameters.JovianTimestamp,

            BatchInboxAddress = batchInboxAddress,
            DepositContractAddress = depositContractAddress,
            L1SystemConfigAddress = systemConfigAddress,

            ChainOpConfig = new OptimismChainConfig
            {
                EIP1559Elasticity = eip1559Elasticity,
                EIP1559Denominator = (ulong)eip1559Denominator,
                EIP1559DenominatorCanyon = (ulong)canyonDenominator
            }
        };
    }
}
