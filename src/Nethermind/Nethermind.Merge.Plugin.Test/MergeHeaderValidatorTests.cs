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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ctx.MergeHeaderValidator.Validate(header, parent, false, out string? error), Is.False);
            Assert.That(error, Is.EqualTo(ZeroDifficultyProofOfWorkError));
        }
    }

    private const string ZeroDifficultyProofOfWorkError = "A proof-of-work block cannot have zero difficulty.";
    private const string NonTerminalParentError = "A proof-of-stake block cannot follow a non-terminal proof-of-work parent.";

    public enum ParentKind
    {
        NonTerminalProofOfWork,
        TerminalProofOfWork,
        ProofOfStake
    }

    /// <summary>Where the parent's total difficulty can be found when the child is validated.</summary>
    public enum ParentTotalDifficultySource
    {
        Header,
        BlockTree,
        BlockTreeWithZeroOnHeader,
        ZeroInBlockTree,
        Unknown
    }

    [Test]
    public void Pos_child_requires_a_terminal_or_pos_parent(
        [Values] ParentKind parentKind,
        [Values] ParentTotalDifficultySource parentTotalDifficultySource,
        [Values] bool childHasTotalDifficulty,
        [Values] bool isUncle)
    {
        const ulong ttd = 10;
        TestSpecProvider specProvider = new(Paris.Instance) { TerminalTotalDifficulty = ttd };
        bool posParent = parentKind == ParentKind.ProofOfStake;
        UInt256 parentTotalDifficulty = parentKind == ParentKind.NonTerminalProofOfWork ? ttd - 1 : ttd;
        BlockHeader parent = Build.A.BlockHeader
            .WithNumber(1)
            .WithDifficulty(posParent ? 0UL : 1UL)
            .WithNonce(posParent ? 0UL : 1000UL)
            .TestObject;
        BlockHeader header = Build.A.BlockHeader
            .WithParent(parent)
            .WithDifficulty(0)
            .WithNonce(0)
            .WithTimestamp(parent.Timestamp + 1)
            .WithBaseFee(BaseFeeCalculator.Calculate(parent, Paris.Instance))
            .TestObject;
        header.TotalDifficulty = childHasTotalDifficulty ? parentTotalDifficulty : null;
        using IContainer ctx = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton<ISpecProvider>(specProvider)
            .AddSingleton(new ChainSpec { Parameters = new ChainParameters { TerminalTotalDifficulty = specProvider.TerminalTotalDifficulty } })
            .AddSingleton<IMergeConfig>(new MergeConfig())
            .AddSingleton<IPoSSwitcher, PoSSwitcher>()
            .AddSingleton<ISealValidator>(Always.Valid)
            .AddSingleton<IHeaderValidator>(Substitute.For<IHeaderValidator>())
            .AddDecorator<IHeaderValidator, MergeHeaderValidator>()
            .Build();

        if (parentTotalDifficultySource is not (ParentTotalDifficultySource.Header or ParentTotalDifficultySource.Unknown))
        {
            // A header inserted before its total difficulty is known is stored with zero.
            parent.TotalDifficulty = parentTotalDifficultySource == ParentTotalDifficultySource.ZeroInBlockTree ? null : parentTotalDifficulty;
            ctx.Resolve<IBlockTree>().Insert(parent, BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded);
        }

        parent.TotalDifficulty = parentTotalDifficultySource switch
        {
            ParentTotalDifficultySource.Header => parentTotalDifficulty,
            ParentTotalDifficultySource.BlockTreeWithZeroOnHeader => UInt256.Zero,
            _ => null
        };

        bool parentTotalDifficultyKnown = parentTotalDifficultySource is ParentTotalDifficultySource.Header
            or ParentTotalDifficultySource.BlockTree or ParentTotalDifficultySource.BlockTreeWithZeroOnHeader;
        // With the TD of both unknown, nothing tells a non-terminal parent apart, so the header is classified by its difficulty.
        bool expectedValid = parentKind != ParentKind.NonTerminalProofOfWork || (!childHasTotalDifficulty && !parentTotalDifficultyKnown);

        bool valid = ctx.Resolve<IHeaderValidator>().Validate(header, parent, isUncle, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.EqualTo(expectedValid));
            Assert.That(error, Is.EqualTo(expectedValid ? null : parentTotalDifficultyKnown ? NonTerminalParentError : ZeroDifficultyProofOfWorkError));
        }
    }

    [TestCase(0UL, null, false, TestName = "{m}(zero TTD)")]
    [TestCase(10UL, 10UL, false, TestName = "{m}(parent TD on the header)")]
    [TestCase(10UL, null, true, TestName = "{m}(parent TD not on the header)")]
    public void Pos_child_reads_the_parent_total_difficulty_from_the_block_tree_only_when_needed(ulong ttd, ulong? parentTotalDifficulty, bool expectLookup)
    {
        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
        poSSwitcher.TerminalTotalDifficulty.Returns((UInt256?)new UInt256(ttd));
        poSSwitcher.IsPostMerge(Arg.Any<BlockHeader>()).Returns(true);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        MergeHeaderValidator validator = new(poSSwitcher, Substitute.For<IHeaderValidator>(), blockTree, MainnetSpecProvider.Instance, Always.Valid, LimboLogs.Instance);
        BlockHeader parent = Build.A.BlockHeader.WithDifficulty(0).TestObject;
        parent.TotalDifficulty = parentTotalDifficulty;
        BlockHeader header = Build.A.BlockHeader.WithParent(parent).WithDifficulty(0).TestObject;

        validator.Validate(header, parent);

        blockTree.ReceivedWithAnyArgs(expectLookup ? 1 : 0).GetInfo(default, default!);
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
