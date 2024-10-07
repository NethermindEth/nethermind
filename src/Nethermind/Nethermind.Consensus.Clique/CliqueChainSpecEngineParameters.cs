// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.Clique;

public class CliqueChainSpecEngineParameters : IChainSpecEngineParameters
{
    public string? SealEngineType => "Clique";

    public ulong Epoch { get; set; }

    public ulong Period { get; set; }

    public UInt256? Reward { get; set; } = UInt256.Zero;

    public void AddTransitions(SortedSet<long> blockNumbers, SortedSet<ulong> timestamps)
    {
    }

    public void ApplyToReleaseSpec(ReleaseSpec spec, long startBlock, ulong? startTimestamp)
    {
    }
}
