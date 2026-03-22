// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Era1;
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
        using MemoryStream stream = new();
        using EraWriter sut = new(stream, Substitute.For<ISpecProvider>());

        Block b0 = Build.A.Block.WithNumber(0).WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
        Block b2 = Build.A.Block.WithNumber(2).WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;

        await sut.Add(b0, []);
        Assert.That(async () => await sut.Add(b2, []), Throws.ArgumentException);
    }

    [Test]
    public Task Add_WithNullBlock_ThrowsArgumentNullException()
    {
        using MemoryStream stream = new();
        using EraWriter sut = new(stream, Substitute.For<ISpecProvider>());

        Assert.That(async () => await sut.Add(null!, []), Throws.ArgumentNullException);
        return Task.CompletedTask;
    }

    [Test]
    public Task Add_PreMergeBlockWithoutTotalDifficulty_ThrowsArgumentException()
    {
        using MemoryStream stream = new();
        using EraWriter sut = new(stream, Substitute.For<ISpecProvider>());

        Block block = Build.A.Block.WithNumber(0).WithTotalDifficulty((UInt256?)null).TestObject;
        Assert.That(async () => await sut.Add(block, []), Throws.ArgumentException);
        return Task.CompletedTask;
    }

    [Test]
    public Task Add_PreMergeBlockWithTdLowerThanDifficulty_ThrowsArgumentOutOfRangeException()
    {
        using MemoryStream stream = new();
        using EraWriter sut = new(stream, Substitute.For<ISpecProvider>());

        Block block = Build.A.Block.WithNumber(0)
            .WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
        block.Header.TotalDifficulty = 0;

        Assert.That(async () => await sut.Add(block, []), Throws.TypeOf<ArgumentOutOfRangeException>());
        return Task.CompletedTask;
    }

    [Test]
    public Task Add_PostMergeBlockWithoutTotalDifficulty_Succeeds()
    {
        using MemoryStream stream = new();
        using EraWriter sut = new(stream, Substitute.For<ISpecProvider>());

        Block block = Build.A.Block.WithNumber(0).WithPostMergeRules().TestObject;
        Assert.That(async () => await sut.Add(block, []), Throws.Nothing);
        return Task.CompletedTask;
    }

    [Test]
    public async Task Add_AfterFinalized_ThrowsEraException()
    {
        using MemoryStream stream = new();
        using EraWriter sut = new(stream, Substitute.For<ISpecProvider>());

        Block block = Build.A.Block.WithNumber(0).WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
        await sut.Add(block, []);
        await sut.Finalize();

        Assert.That(async () => await sut.Add(block, []), Throws.TypeOf<EraException>());
    }

    [Test]
    public async Task Add_WhenExceedingMaxEraSize_ThrowsArgumentException()
    {
        using MemoryStream stream = new();
        using EraWriter sut = new(stream, Substitute.For<ISpecProvider>());

        for (int i = 0; i < EraWriter.MaxEraSize; i++)
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
        using MemoryStream stream = new();
        using EraWriter sut = new(stream, Substitute.For<ISpecProvider>());

        Assert.That(async () => await sut.Finalize(), Throws.TypeOf<EraException>());
    }

    [Test]
    public async Task Finalize_WhenCalledTwice_ThrowsEraException()
    {
        using MemoryStream stream = new();
        using EraWriter sut = new(stream, Substitute.For<ISpecProvider>());

        Block block = Build.A.Block.WithNumber(0).WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
        await sut.Add(block, []);
        await sut.Finalize();

        Assert.That(async () => await sut.Finalize(), Throws.TypeOf<EraException>());
    }

    [Test]
    public async Task Finalize_PreMergeEpoch_ComponentIndexHasFourComponents()
    {
        using TempPath tmpFile = TempPath.GetTempFile();
        using (EraWriter sut = new(tmpFile.Path, Substitute.For<ISpecProvider>()))
        {
            Block block = Build.A.Block.WithNumber(0).WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
            await sut.Add(block, []);
            await sut.Finalize();
        }

        using E2StoreReader reader = new E2StoreReader(tmpFile.Path);
        reader.HasTotalDifficulty.Should().BeTrue("pre-merge epoch has component-count=4");
    }

    [Test]
    public async Task Finalize_PostMergeEpoch_ComponentIndexHasThreeComponents()
    {
        using TempPath tmpFile = TempPath.GetTempFile();
        using (EraWriter sut = new(tmpFile.Path, Substitute.For<ISpecProvider>()))
        {
            Block block = Build.A.Block.WithNumber(0).WithPostMergeRules().TestObject;
            await sut.Add(block, []);
            await sut.Finalize();
        }

        using E2StoreReader reader = new E2StoreReader(tmpFile.Path);
        reader.HasTotalDifficulty.Should().BeFalse("post-merge epoch has component-count=3");
    }

    [Test]
    public async Task Finalize_PreMergeEpoch_AccumulatorRootOffsetIsValid()
    {
        using TempPath tmpFile = TempPath.GetTempFile();
        using (EraWriter sut = new(tmpFile.Path, Substitute.For<ISpecProvider>()))
        {
            Block block = Build.A.Block.WithNumber(0).WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
            await sut.Add(block, []);
            await sut.Finalize();
        }

        using E2StoreReader reader = new E2StoreReader(tmpFile.Path);
        reader.AccumulatorRootOffset.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task Finalize_PostMergeEpoch_AccumulatorRootOffsetIsNegativeOne()
    {
        using TempPath tmpFile = TempPath.GetTempFile();
        using (EraWriter sut = new(tmpFile.Path, Substitute.For<ISpecProvider>()))
        {
            Block block = Build.A.Block.WithNumber(0).WithPostMergeRules().TestObject;
            await sut.Add(block, []);
            await sut.Finalize();
        }

        using E2StoreReader reader = new E2StoreReader(tmpFile.Path);
        reader.AccumulatorRootOffset.Should().Be(-1, "post-merge epoch has no AccumulatorRoot entry");
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

        using E2StoreReader reader = new E2StoreReader(tmpFile.Path);
        reader.HeaderOffset(0).Should().Be(8);
    }

    [Test]
    public void Dispose_WhenCalled_DisposesInnerStream()
    {
        MemoryStream stream = new();
        EraWriter sut = new(stream, Substitute.For<ISpecProvider>());
        sut.Dispose();

        Assert.That(() => stream.ReadByte(), Throws.TypeOf<ObjectDisposedException>());
    }
}
