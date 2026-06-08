// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.ChainSpecStyle;

using System.Collections.Generic;

public interface IChainSpecEngineParameters
{
    string? EngineName { get; }
    string? SealEngineType { get; }
    void ApplyToChainSpec(ChainSpec chainSpec) { }
    void AddTransitions(SortedSet<ulong> blockNumbers, SortedSet<ulong> timestamps) { }
    void ApplyToReleaseSpec(ReleaseSpec spec, ulong startBlock, ulong? startTimestamp) { }
}
