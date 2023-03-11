// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public class MergeHeaderValidatorTests
{
    private class Context
    {
        private IPoSSwitcher? _poSSwitcher;
        public IPoSSwitcher PoSSwitcher => _poSSwitcher ?? Substitute.For<IPoSSwitcher>();

        private IHeaderValidator? _preMergeHeaderValidator;
        public IHeaderValidator PreMergeHeaderValidator => _preMergeHeaderValidator ?? Substitute.For<IHeaderValidator>();

        private IBlockTree? _blockTree;
        public IBlockTree BlockTree => _blockTree ?? Substitute.For<IBlockTree>();

        private ISealValidator? _sealValidator;
        public ISealValidator SealValidator => _sealValidator ?? Substitute.For<ISealValidator>();

        private MergeHeaderValidator? _mergeHeaderValidator = null;
        public MergeHeaderValidator MergeHeaderValidator => _mergeHeaderValidator ?? new MergeHeaderValidator(
            PoSSwitcher,
            PreMergeHeaderValidator,
            BlockTree,
            RopstenSpecProvider.Instance,
            SealValidator,
            LimboLogs.Instance
        );
    }

    [Test]
    public void TestZeroDifficultyPoWBlock()
    {
        BlockHeader parent = Build.A.BlockHeader
            .WithDifficulty(900)
            .WithTotalDifficulty(900)
            .TestObject;

        BlockHeader header = Build.A.BlockHeader
            .WithParent(parent)
            .WithDifficulty(0)
            .WithTotalDifficulty(900)
            .TestObject;

        Context ctx = new Context();
        ctx.PoSSwitcher.IsPostMerge(header).Returns(false);

        ctx.MergeHeaderValidator.Validate(header, parent).Should().BeFalse();
    }
}
