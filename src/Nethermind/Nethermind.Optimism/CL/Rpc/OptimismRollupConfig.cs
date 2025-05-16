// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    public required OptimismGenesis Genesis { get; init; }
    public required ulong BlockTime { get; init; }
    public required ulong MaxSequencerDrift { get; init; }
    public required ulong SeqWindowSize { get; init; }
    public required ulong ChannelTimeoutBedrock { get; init; }
    public required ulong L1ChainID { get; init; }
    public required ulong L2ChainID { get; init; }

    public required ulong? RegolithTime { get; init; }
    public required ulong? CanyonTime { get; init; }
    public required ulong? DeltaTime { get; init; }
    public required ulong? EcotoneTime { get; init; }
    public required ulong? FjordTime { get; init; }
    public required ulong? GraniteTime { get; init; }
    public required ulong? HoloceneTime { get; init; }
    public required ulong? IsthmusTime { get; init; }

    public required Address BatchInboxAddress { get; init; }
    public required Address DepositContractAddress { get; init; }
    public required Address L1SystemConfigAddress { get; init; }

    public required OptimismChainConfig ChainOpConfig { get; init; }

    public sealed record OptimismGenesis
    {
        public required BlockId L1 { get; init; }
        public required BlockId L2 { get; init; }
        public required ulong L2Time { get; init; }
        public required OptimismGenesisSystemConfig SystemConfig { get; init; }

        public sealed record OptimismGenesisSystemConfig
        {
            public required Address BatcherAddr { get; init; }
            public required byte[] Overhead { get; init; }
            public required byte[] Scalar { get; init; }
            public required ulong GasLimit { get; init; }
            public required byte[] EIP1559Params { get; init; }
            public required byte[] OperatorFeeParams { get; init; }
        }
    }

    public sealed record OptimismChainConfig
    {
        public required ulong EIP1559Elasticity { get; init; }
        public required ulong EIP1559Denominator { get; init; }
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
                SystemConfig = new OptimismGenesis.OptimismGenesisSystemConfig
                {
                    // TODO: Extract from superchain-registry
                    BatcherAddr = clParameters.BatcherInboxAddress!,
                    Overhead = new byte[32],
                    Scalar = new byte[32],
                    GasLimit = 0,
                    EIP1559Params = new byte[8],
                    OperatorFeeParams = new byte[32]
                }
            },
            BlockTime = clParameters.L2BlockTime!.Value,
            MaxSequencerDrift = clParameters.MaxSequencerDrift!.Value,
            SeqWindowSize = clParameters.SeqWindowSize!.Value,
            ChannelTimeoutBedrock = 0, // TODO: Figure out
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
            }
        };
    }
}
