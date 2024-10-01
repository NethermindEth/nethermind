// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Optimism;

public class Optimism2ChainSpecEngineParameters : IChainSpecEngineParameters
{
    public string? SealEngineType => "Optimism";

    public long RegolithTimestamp { get; set; }

    public void AddTransitions(SortedSet<long> blockNumbers, SortedSet<ulong> timestamps)
    {

    }

    public void AdjustReleaseSpec(ReleaseSpec spec, long startBlock, ulong? startTimestamp)
    {

    }
}
