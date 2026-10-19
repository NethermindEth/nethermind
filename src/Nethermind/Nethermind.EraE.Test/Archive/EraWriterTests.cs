// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Era1.Exceptions;
using Nethermind.EraE.Proofs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.Int256;
using NSubstitute;
using NUnit.Framework;
using E2StoreReader = Nethermind.EraE.E2Store.E2StoreReader;
using EraWriter = Nethermind.EraE.Archive.EraWriter;

namespace Nethermind.EraE.Test.Archive;

internal class EraWriterTests
{
    [Test]
    public async Task Add_WithNonSequentialBlock_ThrowsArgumentException()
    {
        using EraWriter sut = CreateSut();

        Block b0 = Build.A.Block.WithNumber(0).WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
        Block b2 = Build.A.Block.WithNumber(2).WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;

        await sut.Add(b0, []);
        Assert.That(async () => await sut.Add(b2, []), Throws.ArgumentException);
    }

    [Test]
    public Task Add_WithNullBlock_ThrowsArgumentNullException()
    {
        using EraWriter sut = CreateSut();

        Assert.That(async () => await sut.Add(null!, []), Throws.ArgumentNullException);
        return Task.CompletedTask;
    }

    [Test]
    public Task Add_PreMergeBlockWithoutTotalDifficulty_ThrowsArgumentException()
    {
        using EraWriter sut = CreateSut();

        Block block = Build.A.Block.WithNumber(0).WithTotalDifficulty((UInt256?)null).TestObject;
        Assert.That(async () => await sut.Add(block, []), Throws.ArgumentException);
        return Task.CompletedTask;
    }

    [Test]
    public Task Add_PreMergeBlockWithTdLowerThanDifficulty_ThrowsArgumentOutOfRangeException()
    {
        using EraWriter sut = CreateSut();

        Block block = Build.A.Block.WithNumber(0)
            .WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
        block.Header.TotalDifficulty = 0;

        Assert.That(async () => await sut.Add(block, []), Throws.TypeOf<ArgumentOutOfRangeException>());
        return Task.CompletedTask;
    }

    [Test]
    public Task Add_PostMergeBlockWithoutTotalDifficulty_Succeeds()
    {
        using EraWriter sut = CreateSut();

        Block block = Build.A.Block.WithNumber(0).WithPostMergeRules().TestObject;
        Assert.That(async () => await sut.Add(block, []), Throws.Nothing);
        return Task.CompletedTask;
    }

    [Test]
    public async Task Add_AfterFinalized_ThrowsEraException()
    {
        using EraWriter sut = CreateSut();

        Block block = Build.A.Block.WithNumber(0).WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
        await sut.Add(block, []);
        await sut.Finalize();

        Assert.That(async () => await sut.Add(block, []), Throws.TypeOf<EraException>());
    }

    [Test]
    public async Task Add_WhenExceedingMaxEraSize_ThrowsArgumentException()
    {
        using EraWriter sut = CreateSut();

        for (ulong i = 0; i < EraWriter.MaxEraSize; i++)
        {
            Block block = Build.A.Block.WithNumber(i).WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
            await sut.Add(block, []);
        }

        Block overflow = Build.A.Block.WithNumber(EraWriter.MaxEraSize).WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
        Assert.That(async () => await sut.Add(overflow, []), Throws.ArgumentException);
    }

    [Test]
    public void Finalize_WithNoBlocksAdded_ThrowsEraException()
    {
        using EraWriter sut = CreateSut();

        Assert.That(async () => await sut.Finalize(), Throws.TypeOf<EraException>());
    }

    [Test]
    public async Task Finalize_WhenCalledTwice_ThrowsEraException()
    {
        using EraWriter sut = CreateSut();

        Block block = Build.A.Block.WithNumber(0).WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
        await sut.Add(block, []);
        await sut.Finalize();

        Assert.That(async () => await sut.Finalize(), Throws.TypeOf<EraException>());
    }

    [TestCase(false, TestName = "pre_merge")]
    [TestCase(true, TestName = "post_merge")]
    public async Task Finalize_ComponentIndexHasTotalDifficulty_MatchesMergeState(bool isPostMerge)
    {
        using TempPath tmpFile = TempPath.GetTempFile();
        using (EraWriter sut = new(tmpFile.Path, Substitute.For<ISpecProvider>()))
        {
            Block block = isPostMerge
                ? Build.A.Block.WithNumber(0).WithPostMergeRules().TestObject
                : Build.A.Block.WithNumber(0).WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
            await sut.Add(block, []);
            await sut.Finalize();
        }

        using E2StoreReader reader = new(tmpFile.Path);
        Assert.That(reader.HasTotalDifficulty, Is.EqualTo(!isPostMerge));
    }

    [TestCase(false, TestName = "pre_merge")]
    [TestCase(true, TestName = "post_merge")]
    public async Task Finalize_AccumulatorRootOffset_MatchesMergeState(bool isPostMerge)
    {
        using TempPath tmpFile = TempPath.GetTempFile();
        using (EraWriter sut = new(tmpFile.Path, Substitute.For<ISpecProvider>()))
        {
            Block block = isPostMerge
                ? Build.A.Block.WithNumber(0).WithPostMergeRules().TestObject
                : Build.A.Block.WithNumber(0).WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
            await sut.Add(block, []);
            await sut.Finalize();
        }

        using E2StoreReader reader = new(tmpFile.Path);
        if (isPostMerge)
            Assert.That(reader.AccumulatorRootOffset, Is.EqualTo(-1), "post-merge epoch has no AccumulatorRoot entry");
        else
            Assert.That(reader.AccumulatorRootOffset, Is.GreaterThan(0));
    }

    [Test]
    public async Task Finalize_WithPreMergeBlock_HeaderOffsetStartsAfterVersionEntry()
    {
        using TempPath tmpFile = TempPath.GetTempFile();
        using (EraWriter sut = new(tmpFile.Path, Substitute.For<ISpecProvider>()))
        {
            Block block = Build.A.Block.WithNumber(0).WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
            await sut.Add(block, []);
            await sut.Finalize();
        }

        using E2StoreReader reader = new(tmpFile.Path);
        Assert.That(reader.HeaderOffset(0), Is.EqualTo(8));
    }

    [Test]
    public void Dispose_WhenCalled_DisposesInnerStream()
    {
        MemoryStream stream = new();
        EraWriter sut = new(stream, Substitute.For<ISpecProvider>());
        sut.Dispose();

        Assert.That(() => stream.ReadByte(), Throws.TypeOf<ObjectDisposedException>());
    }

    [TestCase(false, 5L, TestName = "Add_PostMergeBlock_FetchesBeaconRootsForTwelveSecondSlot")]
    [TestCase(true, 6L, TestName = "Add_PostMergeBlock_FetchesBeaconRootsForEip8198Slot")]
    public async Task Add_PostMergeBlock_FetchesBeaconRootsForItsSlot(bool eip8198Scheduled, long expectedSlot)
    {
        const ulong genesis = 100;
        const ulong fork = genesis + 5 * 12;
        (ForkActivation, IReleaseSpec)[] transitions = eip8198Scheduled
            ? [((ForkActivation)0, London.Instance), (new ForkActivation(0, fork), new OverridableReleaseSpec(London.Instance) { IsEip8198Enabled = true })]
            : [((ForkActivation)0, London.Instance)];
        CustomSpecProvider specProvider = new(transitions) { BeaconChainGenesisTimestamp = genesis, Eip8198SlotDurationMs = 10_000 };
        IBeaconRootsProvider beaconRoots = Substitute.For<IBeaconRootsProvider>();
        using EraWriter sut = new(new MemoryStream(), specProvider, beaconRoots);

        await sut.Add(Build.A.Block.WithNumber(0).WithTimestamp(fork - 12).WithPostMergeRules().TestObject, []);
        await sut.Add(Build.A.Block.WithNumber(1).WithTimestamp(fork + 10).WithPostMergeRules().TestObject, []);

        await beaconRoots.Received(1).GetBeaconRoots(4, Arg.Any<CancellationToken>());
        await beaconRoots.Received(1).GetBeaconRoots(expectedSlot, Arg.Any<CancellationToken>());
    }

    private static EraWriter CreateSut() => new(new MemoryStream(), Substitute.For<ISpecProvider>());
}
