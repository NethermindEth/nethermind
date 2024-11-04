// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Optimism;

public class OptimismChainSpecEngineParameters : IChainSpecEngineParameters
{
    public string? EngineName => SealEngineType;
    public string? SealEngineType => Core.SealEngineType.Optimism;

    public ulong? RegolithTimestamp { get; set; }

    public long? BedrockBlockNumber { get; set; }

    public ulong? CanyonTimestamp { get; set; }

    public ulong? EcotoneTimestamp { get; set; }

    public ulong? FjordTimestamp { get; set; }

    public ulong? GraniteTimestamp { get; set; }

    public Address? L1FeeRecipient { get; set; }

    public Address? L1BlockAddress { get; set; }

    public UInt256? CanyonBaseFeeChangeDenominator { get; set; }

    public Address? Create2DeployerAddress { get; set; }

    public byte[]? Create2DeployerCode { get; set; }

    public void AddTransitions(SortedSet<long> blockNumbers, SortedSet<ulong> timestamps)
    {
        ArgumentNullException.ThrowIfNull(BedrockBlockNumber);
        ArgumentNullException.ThrowIfNull(RegolithTimestamp);
        ArgumentNullException.ThrowIfNull(CanyonTimestamp);
        ArgumentNullException.ThrowIfNull(EcotoneTimestamp);
        ArgumentNullException.ThrowIfNull(FjordTimestamp);
        ArgumentNullException.ThrowIfNull(GraniteTimestamp);
        blockNumbers.Add(BedrockBlockNumber.Value);
        timestamps.Add(RegolithTimestamp.Value);
        timestamps.Add(CanyonTimestamp.Value);
        timestamps.Add(EcotoneTimestamp.Value);
        timestamps.Add(FjordTimestamp.Value);
        timestamps.Add(GraniteTimestamp.Value);
    }

    public void ApplyToReleaseSpec(ReleaseSpec spec, long startBlock, ulong? startTimestamp)
    {
        ArgumentNullException.ThrowIfNull(CanyonBaseFeeChangeDenominator);
        if (CanyonTimestamp <= startTimestamp)
        {
            spec.BaseFeeMaxChangeDenominator = CanyonBaseFeeChangeDenominator.Value;
        }
    }
}
