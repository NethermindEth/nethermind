// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System.Collections.Generic;

namespace Nethermind.Specs.ChainSpecStyle;

public interface IChainSpecEngineParameters
{
    string? SealEngineType { get; }
    void AddTransitions(SortedSet<long> blockNumbers, SortedSet<ulong> timestamps);
    void AdjustReleaseSpec(ReleaseSpec spec, long startBlock, ulong? startTimestamp);
}
