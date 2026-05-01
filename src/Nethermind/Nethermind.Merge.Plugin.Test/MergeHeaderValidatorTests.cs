// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public class MergeHeaderValidatorTests
{
    private class Context
    {
        public IPoSSwitcher PoSSwitcher => Substitute.For<IPoSSwitcher>();
        public IHeaderValidator PreMergeHeaderValidator => Substitute.For<IHeaderValidator>();

        public IBlockTree BlockTree
        {
            get
            {
                IBlockTree blockTree = Substitute.For<IBlockTree>();
                blockTree.GetTotalDifficulty(Arg.Any<BlockHeader?>()).Returns(ci =>
                {
                    BlockHeader? h = (BlockHeader?)ci[0];
                    return h is null ? null : (UInt256?)h.Difficulty;
                });
                return blockTree;
            }
        }

        public ISealValidator SealValidator => Substitute.For<ISealValidator>();

        public MergeHeaderValidator MergeHeaderValidator => new(
            PoSSwitcher,
            PreMergeHeaderValidator,
            BlockTree,
            MainnetSpecProvider.Instance,
            SealValidator,
            LimboLogs.Instance
        );
    }

    [Test]
    public void TestZeroDifficultyPoWBlock()
    {
        BlockHeader parent = Build.A.BlockHeader
            .WithDifficulty(900)
            .TestObject;

        BlockHeader header = Build.A.BlockHeader
            .WithParent(parent)
            .WithDifficulty(0)
            .TestObject;

        Context ctx = new();
        ctx.PoSSwitcher.IsPostMerge(header).Returns(false);

        ctx.MergeHeaderValidator.Validate(header, parent).Should().BeFalse();
    }
}
