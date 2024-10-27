// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Specs.ChainSpecStyle;

public class NethDevChainSpecEngineParameters : IChainSpecEngineParameters
{
    public string? EngineName => "NethDev";
    public string? SealEngineType => "NethDev";

    public void AddTransitions(SortedSet<long> blockNumbers, SortedSet<ulong> timestamps)
    {
    }

    public void ApplyToReleaseSpec(ReleaseSpec spec, long startBlock, ulong? startTimestamp)
    {
    }

    public void ApplyToChainSpec(ChainSpec chainSpec)
    {
    }
}
