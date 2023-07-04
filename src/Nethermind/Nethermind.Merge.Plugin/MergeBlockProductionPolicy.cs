// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Producers;

namespace Nethermind.Merge.Plugin;

public class MergeBlockProductionPolicy : IMergeBlockProductionPolicy
{
    private readonly IBlockProductionPolicy _preMergeBlockProductionPolicy;

    public MergeBlockProductionPolicy(IBlockProductionPolicy preMergeBlockProductionPolicy)
    {
        _preMergeBlockProductionPolicy = preMergeBlockProductionPolicy;
    }

    public bool ShouldStartBlockProduction() => true;

    public bool ShouldInitPreMergeBlockProduction() => _preMergeBlockProductionPolicy.ShouldStartBlockProduction();
}

public interface IMergeBlockProductionPolicy : IBlockProductionPolicy
{
    public bool ShouldInitPreMergeBlockProduction();
}
