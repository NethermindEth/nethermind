// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Messages;
using Nethermind.Core.Test;
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
        using IContainer ctx = BuildContainer(specProvider, Substitute.For<IHeaderValidator>());

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
        // With neither total difficulty known the parent rule cannot apply, and the header is validated by its difficulty as before.
        bool expectedValid = parentKind != ParentKind.NonTerminalProofOfWork || (!childHasTotalDifficulty && !parentTotalDifficultyKnown);

        bool valid = ctx.Resolve<IHeaderValidator>().Validate(header, parent, isUncle, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.EqualTo(expectedValid));
            Assert.That(error, Is.EqualTo(expectedValid ? null : parentTotalDifficultyKnown ? NonTerminalParentError : ZeroDifficultyProofOfWorkError));
        }
    }

    [TestCase(0UL, null, false, 0UL, TestName = "{m}(zero TTD)")]
    [TestCase(10UL, 10UL, false, 0UL, TestName = "{m}(parent TD on the header)")]
    [TestCase(10UL, null, true, 0UL, TestName = "{m}(parent TD not on the header)")]
    [TestCase(10UL, null, false, 1UL, TestName = "{m}(PoW header)")]
    public void Pos_child_reads_the_parent_total_difficulty_from_the_block_tree_only_when_needed(ulong ttd, ulong? parentTotalDifficulty, bool expectLookup, ulong headerDifficulty)
    {
        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
        poSSwitcher.TerminalTotalDifficulty.Returns((UInt256?)new UInt256(ttd));
        poSSwitcher.IsPostMerge(Arg.Any<BlockHeader>()).Returns(true);
        poSSwitcher.GetBlockConsensusInfo(Arg.Any<BlockHeader>()).Returns((false, true));
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        MergeHeaderValidator validator = new(poSSwitcher, Substitute.For<IHeaderValidator>(), blockTree, MainnetSpecProvider.Instance, Always.Valid, LimboLogs.Instance);
        BlockHeader parent = Build.A.BlockHeader.WithDifficulty(0).TestObject;
        parent.TotalDifficulty = parentTotalDifficulty;
        poSSwitcher.GetBlockConsensusInfo(parent).Returns((false, false));
        BlockHeader header = Build.A.BlockHeader.WithParent(parent).WithDifficulty(headerDifficulty).TestObject;

        validator.Validate(header, parent);

        blockTree.ReceivedWithAnyArgs(expectLookup ? 1 : 0).GetInfo(default, default!);
    }

    [Test]
    public void Orphaned_pos_header_is_validated_without_the_parent_rule()
    {
        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
        poSSwitcher.TerminalTotalDifficulty.Returns((UInt256?)new UInt256(10));
        poSSwitcher.IsPostMerge(Arg.Any<BlockHeader>()).Returns(true);
        poSSwitcher.GetBlockConsensusInfo(Arg.Any<BlockHeader>()).Returns((false, true));
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        MergeHeaderValidator validator = new(poSSwitcher, Substitute.For<IHeaderValidator>(), blockTree, MainnetSpecProvider.Instance, Always.Valid, LimboLogs.Instance);
        BlockHeader header = Build.A.BlockHeader.WithDifficulty(0).WithNonce(0).TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(validator.ValidateOrphaned(header, out string? error), Is.True, error);
            Assert.That(() => blockTree.DidNotReceiveWithAnyArgs().GetInfo(default, default!), Throws.Nothing);
        }
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
        using IContainer ctx = BuildContainer(specProvider, preMergeValidator);

        Assert.That(ctx.Resolve<IHeaderValidator>().Validate(header, parent), Is.EqualTo(powChildAllowed));
    }

    private static IContainer BuildContainer(TestSpecProvider specProvider, IHeaderValidator preMergeValidator) =>
        new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton<ISpecProvider>(specProvider)
            .AddSingleton(new ChainSpec { Parameters = new ChainParameters { TerminalTotalDifficulty = specProvider.TerminalTotalDifficulty } })
            .AddSingleton<IMergeConfig>(new MergeConfig())
            .AddSingleton<IPoSSwitcher, PoSSwitcher>()
            .AddSingleton<ISealValidator>(Always.Valid)
            .AddSingleton<IHeaderValidator>(preMergeValidator)
            .AddDecorator<IHeaderValidator, MergeHeaderValidator>()
            .Build();

    [Test]
    public void Post_merge_header_validation_does_not_allocate()
    {
        const int Iterations = 1_000;
        const int Windows = 5;
        const ulong ttd = 10;
        TestSpecProvider specProvider = new(Paris.Instance) { TerminalTotalDifficulty = ttd };
        BlockHeader parent = Build.A.BlockHeader
            .WithNumber(1)
            .WithDifficulty(0)
            .WithNonce(0)
            .WithTotalDifficulty(ttd)
            .TestObject;
        BlockHeader header = Build.A.BlockHeader
            .WithParent(parent)
            .WithDifficulty(0)
            .WithNonce(0)
            .WithTimestamp(parent.Timestamp + 1)
            .WithBaseFee(BaseFeeCalculator.Calculate(parent, Paris.Instance))
            .WithTotalDifficulty(ttd)
            .TestObject;
        using IContainer ctx = BuildContainer(specProvider, Substitute.For<IHeaderValidator>());
        IHeaderValidator validator = ctx.Resolve<IHeaderValidator>();
        Assert.That(validator.Validate(header, parent, false, out string? error, false), Is.True, error);

        long allocated = long.MaxValue;
        for (int window = 0; window < Windows; window++)
        {
            long before = System.GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Iterations; i++) validator.Validate(header, parent, false, out _, false);
            allocated = Math.Min(allocated, System.GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.That(allocated, Is.Zero);
    }

    [TestCase(true, BlockErrorMessages.InvalidPostMergeNonce, TestName = "{m}(nonce)")]
    [TestCase(false, BlockErrorMessages.InvalidPostMergeUnclesHash, TestName = "{m}(uncles hash)")]
    public void Post_merge_header_with_a_wrong_pow_field_is_rejected(bool wrongNonce, string expectedError)
    {
        const ulong ttd = 10;
        TestSpecProvider specProvider = new(Paris.Instance) { TerminalTotalDifficulty = ttd };
        BlockHeader parent = Build.A.BlockHeader
            .WithNumber(1)
            .WithDifficulty(0)
            .WithNonce(0)
            .WithTotalDifficulty(ttd)
            .TestObject;
        BlockHeaderBuilder headerBuilder = Build.A.BlockHeader
            .WithParent(parent)
            .WithDifficulty(0)
            .WithNonce(wrongNonce ? 1UL : 0UL)
            .WithTimestamp(parent.Timestamp + 1)
            .WithBaseFee(BaseFeeCalculator.Calculate(parent, Paris.Instance))
            .WithTotalDifficulty(ttd);
        if (!wrongNonce) headerBuilder = headerBuilder.WithUnclesHash(Keccak.Zero);
        BlockHeader header = headerBuilder.TestObject;
        using IContainer ctx = BuildContainer(specProvider, Substitute.For<IHeaderValidator>());

        bool valid = ctx.Resolve<IHeaderValidator>().Validate(header, parent, false, out string? error, false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.False);
            Assert.That(error, Is.EqualTo(expectedError));
        }
    }

    private const ulong SubstituteTtd = 10;

    // difficulty, nonce, wrong uncles hash, total difficulty (0 = not known), header flagged post-merge, expected error
    [TestCase(1UL, 0UL, false, 0UL, true, BlockErrorMessages.InvalidPostMergeDifficulty, TestName = "{m}(difficulty)")]
    [TestCase(0UL, 1UL, false, 0UL, true, BlockErrorMessages.InvalidPostMergeNonce, TestName = "{m}(nonce)")]
    [TestCase(0UL, 0UL, true, 0UL, true, BlockErrorMessages.InvalidPostMergeUnclesHash, TestName = "{m}(uncles hash)")]
    [TestCase(1UL, 1UL, true, 0UL, true, BlockErrorMessages.InvalidPostMergeDifficulty, TestName = "{m}(all three fields)")]
    [TestCase(0UL, 1UL, true, 0UL, true, BlockErrorMessages.InvalidPostMergeNonce, TestName = "{m}(nonce and uncles hash)")]
    [TestCase(0UL, 0UL, false, 9UL, true, BlockErrorMessages.InvalidTotalDifficulty, TestName = "{m}(total difficulty below TTD)")]
    [TestCase(0UL, 0UL, false, 10UL, false, BlockErrorMessages.InvalidTotalDifficulty, TestName = "{m}(total difficulty at TTD on a header not flagged post-merge)")]
    [TestCase(0UL, 1UL, false, 9UL, true, BlockErrorMessages.InvalidTotalDifficulty, TestName = "{m}(total difficulty and nonce)")]
    public void Orphaned_post_merge_header_breaking_a_merge_rule_is_rejected_with_its_error(
        ulong difficulty, ulong nonce, bool wrongUnclesHash, ulong totalDifficulty, bool flaggedPostMerge, string expectedError)
    {
        BlockHeader header = OrphanedHeader(difficulty, nonce, wrongUnclesHash, totalDifficulty, flaggedPostMerge);

        // Warnings are off, so the error must not depend on them.
        bool valid = PostMergeValidator(NullLogManager.Instance).ValidateOrphaned(header, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.False);
            Assert.That(error, Is.EqualTo(expectedError));
        }
    }

    [TestCase(false, TestName = "{m}(uncles hash valid)")]
    [TestCase(true, TestName = "{m}(uncles hash wrong too)")]
    public void Orphaned_post_merge_header_breaking_the_total_difficulty_and_nonce_rules_warns_for_both_in_order(bool wrongUnclesHash)
    {
        TestLogger logger = new() { IsTrace = false, IsDebug = false, IsInfo = false, IsError = false };
        BlockHeader header = OrphanedHeader(0, 1, wrongUnclesHash, SubstituteTtd - 1, true);

        bool valid = PostMergeValidator(new OneLoggerLogManager(new ILogger(logger))).ValidateOrphaned(header, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.False);
            Assert.That(error, Is.EqualTo(BlockErrorMessages.InvalidTotalDifficulty));
            // The field checks still run after the total difficulty check fails, and stop at the first wrong field.
            Assert.That(logger.LogList, Has.Count.EqualTo(2), string.Join(Environment.NewLine, logger.LogList));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(logger.LogList[0], Does.Contain("total difficulty is incorrect because of TTD"));
            Assert.That(logger.LogList[1], Does.Contain("the Nonce is incorrect"));
        }
    }

    [Test]
    public void Orphaned_post_merge_header_with_a_wrong_difficulty_and_nonce_warns_only_for_the_difficulty()
    {
        TestLogger logger = new() { IsTrace = false, IsDebug = false, IsInfo = false, IsError = false };
        BlockHeader header = OrphanedHeader(1, 1, false, SubstituteTtd, true);

        bool valid = PostMergeValidator(new OneLoggerLogManager(new ILogger(logger))).ValidateOrphaned(header, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.False);
            Assert.That(error, Is.EqualTo(BlockErrorMessages.InvalidPostMergeDifficulty));
            // The total difficulty check passes, and the field checks stop at the first wrong field.
            Assert.That(logger.LogList, Has.Count.EqualTo(1), string.Join(Environment.NewLine, logger.LogList));
        }

        Assert.That(logger.LogList[0], Does.Contain("the Difficulty is incorrect"));
    }

    [TestCase(10UL, true, TestName = "{m}(total difficulty at TTD)")]
    [TestCase(9UL, false, TestName = "{m}(total difficulty below TTD on a header not flagged post-merge)")]
    public void Orphaned_post_merge_header_keeping_the_merge_rules_is_valid(ulong totalDifficulty, bool flaggedPostMerge)
    {
        BlockHeader header = OrphanedHeader(0, 0, false, totalDifficulty, flaggedPostMerge);

        bool valid = PostMergeValidator(NullLogManager.Instance).ValidateOrphaned(header, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.True, error);
            Assert.That(error, Is.Null);
        }
    }

    /// <summary>A validator whose switcher classifies every header as post-merge and not terminal.</summary>
    private static MergeHeaderValidator PostMergeValidator(ILogManager logManager)
    {
        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
        poSSwitcher.TerminalTotalDifficulty.Returns((UInt256?)new UInt256(SubstituteTtd));
        poSSwitcher.IsPostMerge(Arg.Any<BlockHeader>()).Returns(true);
        poSSwitcher.GetBlockConsensusInfo(Arg.Any<BlockHeader>()).Returns((false, true));
        return new(poSSwitcher, Substitute.For<IHeaderValidator>(), Substitute.For<IBlockTree>(), MainnetSpecProvider.Instance, Always.Valid, logManager);
    }

    /// <summary>The header of <see cref="Orphaned_pos_header_is_validated_without_the_parent_rule"/> with the given merge fields.</summary>
    private static BlockHeader OrphanedHeader(ulong difficulty, ulong nonce, bool wrongUnclesHash, ulong totalDifficulty, bool flaggedPostMerge)
    {
        BlockHeaderBuilder builder = Build.A.BlockHeader.WithDifficulty(difficulty).WithNonce(nonce);
        if (wrongUnclesHash) builder = builder.WithUnclesHash(Keccak.Zero);
        BlockHeader header = builder.TestObject;
        header.TotalDifficulty = totalDifficulty == 0 ? null : (UInt256)totalDifficulty;
        // PoSSwitcher writes its verdict back to the header; the substitute does not.
        header.IsPostMerge = flaggedPostMerge;
        return header;
    }
}
