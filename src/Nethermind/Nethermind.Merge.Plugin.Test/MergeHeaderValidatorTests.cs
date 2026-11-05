// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Core.Specs;
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

        public IBlockTree BlockTree => Substitute.For<IBlockTree>();

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
            .WithTotalDifficulty(900)
            .TestObject;

        BlockHeader header = Build.A.BlockHeader
            .WithParent(parent)
            .WithDifficulty(0)
            .WithTotalDifficulty(900)
            .TestObject;

        Context ctx = new();
        ctx.PoSSwitcher.IsPostMerge(header).Returns(false);

        Assert.That(ctx.MergeHeaderValidator.Validate(header, parent), Is.False);
    }

    public enum ParentConsensus
    {
        PreMergeGenesis,
        ZeroDifficultyPreMergeGenesis,
        PreMerge,
        BeforeTerminal,
        Terminal,
        PostMerge,
        PostMergeGenesis
    }

    [Test]
    public void Pow_child_requires_a_pre_terminal_parent(
        [Values] ParentConsensus parentConsensus,
        [Values] bool childHasTotalDifficulty)
    {
        bool postMergeParent = parentConsensus is ParentConsensus.PostMerge or ParentConsensus.PostMergeGenesis;
        bool powChildAllowed = !postMergeParent && parentConsensus != ParentConsensus.Terminal;
        bool genesis = parentConsensus is ParentConsensus.PreMergeGenesis or ParentConsensus.ZeroDifficultyPreMergeGenesis or ParentConsensus.PostMergeGenesis;
        TestSpecProvider specProvider = new(Paris.Instance)
        {
            TerminalTotalDifficulty = parentConsensus == ParentConsensus.PostMergeGenesis ? 0UL : 10UL
        };
        BlockHeader parent = Build.A.BlockHeader
            .WithNumber(genesis ? 0UL : 1UL)
            .WithDifficulty(postMergeParent || parentConsensus == ParentConsensus.ZeroDifficultyPreMergeGenesis ? 0UL : 1UL)
            .WithNonce(postMergeParent ? 0UL : 1000UL)
            .WithTotalDifficulty(parentConsensus is ParentConsensus.PostMergeGenesis or ParentConsensus.ZeroDifficultyPreMergeGenesis ? 0UL :
                parentConsensus is ParentConsensus.PostMerge or ParentConsensus.Terminal ? 10UL :
                parentConsensus == ParentConsensus.BeforeTerminal ? 9UL : 1UL)
            .TestObject;
        BlockHeader header = Build.A.BlockHeader.WithParent(parent).WithDifficulty(1).WithTimestamp(parent.Timestamp + 1).TestObject;
        header.TotalDifficulty = childHasTotalDifficulty ? parent.TotalDifficulty + header.Difficulty : null;
        IHeaderValidator preMergeValidator = Substitute.For<IHeaderValidator>();
        preMergeValidator.Validate(header, parent, false, out Arg.Any<string?>(), true).Returns(true);
        using IContainer ctx = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton<ISpecProvider>(specProvider)
            .AddSingleton(new ChainSpec { Parameters = new ChainParameters { TerminalTotalDifficulty = specProvider.TerminalTotalDifficulty } })
            .AddSingleton<IMergeConfig>(new MergeConfig())
            .AddSingleton<IPoSSwitcher, PoSSwitcher>()
            .AddSingleton<ISealValidator>(Always.Valid)
            .AddSingleton<IHeaderValidator>(preMergeValidator)
            .AddDecorator<IHeaderValidator, MergeHeaderValidator>()
            .Build();

        Assert.That(ctx.Resolve<IHeaderValidator>().Validate(header, parent), Is.EqualTo(powChildAllowed));
    }
}
