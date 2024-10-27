// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Taiko;

public class TaikoChainSpecEngineParameters : IChainSpecEngineParameters
{
    public string? SealEngineType => "Taiko";
    public void ApplyToChainSpec(ChainSpec chainSpec)
    {
    }

    public void AddTransitions(SortedSet<long> blockNumbers, SortedSet<ulong> timestamps)
    {
    }

    public void ApplyToReleaseSpec(ReleaseSpec spec, long startBlock, ulong? startTimestamp)
    {
    }
}
