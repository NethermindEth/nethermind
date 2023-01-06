// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Producers;

namespace Nethermind.Merge.Plugin.BlockProduction;

public interface IMergeBlockProductionPolicy : IBlockProductionPolicy
{
    public bool ShouldInitPreMergeBlockProduction();
}
