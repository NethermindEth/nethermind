// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.ChainSpecStyle;

using System.Collections.Generic;

public interface IChainSpecEngineParameters
{
    string? SealEngineType { get; }

    void AddTransitions(SortedSet<long> blockNumbers, SortedSet<ulong> timestamps);

    void AdjustReleaseSpec(ReleaseSpec spec, long startBlock, ulong? startTimestamp);
}
