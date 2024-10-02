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
    public string? SealEngineType => "Optimism";

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
        ArgumentNullException.ThrowIfNull(RegolithTimestamp);
        ArgumentNullException.ThrowIfNull(BedrockBlockNumber);
        ArgumentNullException.ThrowIfNull(CanyonTimestamp);
        ArgumentNullException.ThrowIfNull(EcotoneTimestamp);
        ArgumentNullException.ThrowIfNull(FjordTimestamp);
        ArgumentNullException.ThrowIfNull(GraniteTimestamp);
        ArgumentNullException.ThrowIfNull(L1FeeRecipient);
        ArgumentNullException.ThrowIfNull(L1BlockAddress);
        ArgumentNullException.ThrowIfNull(CanyonBaseFeeChangeDenominator);
        ArgumentNullException.ThrowIfNull(Create2DeployerAddress);
        ArgumentNullException.ThrowIfNull(Create2DeployerCode);
    }

    public void AdjustReleaseSpec(ReleaseSpec spec, long startBlock, ulong? startTimestamp)
    {
        if (CanyonTimestamp <= startTimestamp)
        {
            // TODO: check
            spec.BaseFeeMaxChangeDenominator = CanyonBaseFeeChangeDenominator!.Value;
        }
    }
}
