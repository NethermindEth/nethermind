// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public class BlockForRpcTests
{
    [Test]
    public void TotalDifficulty_is_included_on_non_merge_chains()
    {
        Block block = Build.A.Block.WithNumber(1).WithTotalDifficulty(0x1000L).TestObject;
        TestSpecProvider specProvider = new(London.Instance); // no merge configured -> MergeBlockNumber is null

        BlockForRpc rpc = new(block, false, specProvider);

        Assert.That(rpc.TotalDifficulty, Is.EqualTo((UInt256)0x1000));
    }

    [Test]
    public void TotalDifficulty_is_omitted_on_merge_chains()
    {
        // Regression for the Gnosis Hive rpc-compat failures: post-merge block responses must omit
        // totalDifficulty. A set MergeBlockNumber (e.g. 0 for a merge-at-genesis chain) marks the
        // chain post-merge, matching geth/execution-apis.
        Block block = Build.A.Block.WithNumber(1).WithTotalDifficulty(0x1000L).TestObject;
        TestSpecProvider specProvider = new(London.Instance);
        specProvider.UpdateMergeTransitionInfo(0);

        BlockForRpc rpc = new(block, false, specProvider);

        Assert.That(rpc.TotalDifficulty, Is.Null);
    }
}
