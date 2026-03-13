// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.EraE.Config;
using EraException = Nethermind.Era1.EraException;
using Nethermind.EraE.Export;
using Nethermind.EraE.Store;
using NUnit.Framework;

namespace Nethermind.EraE.Test.Store;

public class EraStoreTests
{
    [TestCase(100, 16, 32)]
    [TestCase(100, 16, 64)]
    [TestCase(200, 50, 100)]
    public async Task FirstAndLastBlock_AfterExport_MatchSpecifiedRange(int chainLength, int from, int to)
    {
        await using IContainer ctx = await EraETestModule.CreateExportedEraEnv(chainLength, from, to);
        string tmpDirectory = ctx.ResolveTempDirPath();

        using IEraStore eraStore = ctx.Resolve<IEraStoreFactory>().Create(tmpDirectory, null);

        eraStore.FirstBlock.Should().Be(from);
        eraStore.LastBlock.Should().Be(to);
    }

    [Test]
    public async Task FindBlockAndReceipts_WithKnownBlockNumber_ReturnsBlock()
    {
        const int chainLength = 32;
        await using IContainer ctx = await EraETestModule.CreateExportedEraEnv(chainLength, from: 0, to: 0);
        string tmpDirectory = ctx.ResolveTempDirPath();

        using IEraStore eraStore = ctx.Resolve<IEraStoreFactory>().Create(tmpDirectory, null);

        (Block? block, TxReceipt[]? receipts) = await eraStore.FindBlockAndReceipts(
            eraStore.FirstBlock, ensureValidated: false);

        block.Should().NotBeNull();
        receipts.Should().NotBeNull();
    }

    [Test]
    public async Task FindBlockAndReceipts_WithOutOfRangeNumber_ReturnsNull()
    {
        const int chainLength = 32;
        await using IContainer ctx = await EraETestModule.CreateExportedEraEnv(chainLength, from: 0, to: 0);
        string tmpDirectory = ctx.ResolveTempDirPath();

        using IEraStore eraStore = ctx.Resolve<IEraStoreFactory>().Create(tmpDirectory, null);

        (Block? block, TxReceipt[]? receipts) = await eraStore.FindBlockAndReceipts(99999, ensureValidated: false);

        block.Should().BeNull();
        receipts.Should().BeNull();
    }

    [Test]
    public async Task FindBlockAndReceipts_WithValidBlockNumber_ReturnsCorrectBlock()
    {
        const int chainLength = 50;
        await using IContainer ctx = await EraETestModule.CreateExportedEraEnv(chainLength, from: 0, to: 0);
        string tmpDirectory = ctx.ResolveTempDirPath();

        using IEraStore eraStore = ctx.Resolve<IEraStoreFactory>().Create(tmpDirectory, null);

        long targetBlock = eraStore.FirstBlock + 5;
        (Block? block, _) = await eraStore.FindBlockAndReceipts(targetBlock, ensureValidated: false);

        block!.Number.Should().Be(targetBlock);
    }

    [Test]
    public async Task FindBlockAndReceipts_WithNegativeBlockNumber_ThrowsArgumentOutOfRangeException()
    {
        const int chainLength = 32;
        await using IContainer ctx = await EraETestModule.CreateExportedEraEnv(chainLength, from: 0, to: 0);
        string tmpDirectory = ctx.ResolveTempDirPath();

        using IEraStore eraStore = ctx.Resolve<IEraStoreFactory>().Create(tmpDirectory, null);

        Assert.That(
            async () => await eraStore.FindBlockAndReceipts(-1, ensureValidated: false),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public async Task NextEraStart_WhenCalled_ReturnsCorrectBoundary()
    {
        const int eraSize = 16;
        const int chainLength = 50;
        await using IContainer ctx = await EraETestModule
            .BuildContainerBuilderWithBlockTreeOfLength(chainLength)
            .AddSingleton<IEraEConfig>(new EraEConfig { MaxEraSize = eraSize, NetworkName = EraETestModule.TestNetwork })
            .Build()
            .AsTask();

        IEraExporter exporter = ctx.Resolve<IEraExporter>();
        await exporter.Export(ctx.ResolveTempDirPath(), 0, 0);
        string tmpDirectory = ctx.ResolveTempDirPath();

        using IEraStore eraStore = ctx.Resolve<IEraStoreFactory>().Create(tmpDirectory, null);
        long nextStart = eraStore.NextEraStart(eraStore.FirstBlock);

        nextStart.Should().Be(eraSize, "first era covers blocks 0..eraSize-1");
    }

    [Test]
    public void Constructor_WithDirectoryContainingNoEraFiles_ThrowsEraException()
    {
        using IContainer ctx = EraETestModule.BuildContainerBuilder().Build();
        string tmpDirectory = ctx.ResolveTempDirPath();
        System.IO.Directory.CreateDirectory(tmpDirectory);

        System.IO.File.WriteAllText(System.IO.Path.Combine(tmpDirectory, EraExporter.ChecksumsFileName), "");

        Assert.That(
            () => ctx.Resolve<IEraStoreFactory>().Create(tmpDirectory, null),
            Throws.TypeOf<EraException>());
    }

    [Test]
    public async Task FindBlockAndReceipts_WithValidTrustedAccumulators_Succeeds()
    {
        const int chainLength = 32;
        await using IContainer ctx = await EraETestModule.CreateExportedEraEnv(chainLength, from: 0, to: 0);
        string tmpDirectory = ctx.ResolveTempDirPath();

        string accPath = System.IO.Path.Combine(tmpDirectory, EraExporter.AccumulatorFileName);
        ISet<ValueHash256> trusted = (await System.IO.File.ReadAllLinesAsync(accPath))
            .Select(EraPathUtils.ExtractHashFromChecksumEntry)
            .ToHashSet();

        using IEraStore eraStore = ctx.Resolve<IEraStoreFactory>().Create(tmpDirectory, trusted);

        Assert.That(
            async () => await eraStore.FindBlockAndReceipts(eraStore.FirstBlock, ensureValidated: true),
            Throws.Nothing);
    }
}

file static class ContainerAsyncExtension
{
    public static Task<IContainer> AsTask(this IContainer container) => Task.FromResult(container);
}
