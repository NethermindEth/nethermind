// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
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

        eraStore.BlockRange.Should().Be((from, to));
    }

    [Test]
    public async Task FindBlockAndReceipts_WithKnownBlockNumber_ReturnsBlock()
    {
        await using EraStoreEnv env = await CreateDefaultEraStoreEnv();

        (Block? block, TxReceipt[]? receipts) = await env.EraStore.FindBlockAndReceipts(
            env.EraStore.BlockRange.First, ensureValidated: false);

        block.Should().NotBeNull();
        receipts.Should().NotBeNull();
    }

    [Test]
    public async Task FindBlockAndReceipts_WithOutOfRangeNumber_ReturnsNull()
    {
        await using EraStoreEnv env = await CreateDefaultEraStoreEnv();

        (Block? block, TxReceipt[]? receipts) = await env.EraStore.FindBlockAndReceipts(99999, ensureValidated: false);

        block.Should().BeNull();
        receipts.Should().BeNull();
    }

    [Test]
    public async Task FindBlockAndReceipts_WithValidBlockNumber_ReturnsCorrectBlock()
    {
        await using EraStoreEnv env = await CreateDefaultEraStoreEnv();

        long targetBlock = env.EraStore.BlockRange.First + 5;
        (Block? block, _) = await env.EraStore.FindBlockAndReceipts(targetBlock, ensureValidated: false);

        block!.Number.Should().Be(targetBlock);
    }

    [Test]
    public async Task FindBlockAndReceipts_WithNegativeBlockNumber_ThrowsArgumentOutOfRangeException()
    {
        await using EraStoreEnv env = await CreateDefaultEraStoreEnv();

        Assert.That(
            async () => await env.EraStore.FindBlockAndReceipts(-1, ensureValidated: false),
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
        long nextStart = eraStore.NextEraStart(eraStore.BlockRange.First);

        nextStart.Should().Be(eraSize, "first era covers blocks 0..eraSize-1");
    }

    [Test]
    public void Constructor_WithDirectoryContainingNoEraFiles_ThrowsEraException()
    {
        using IContainer ctx = EraETestModule.BuildContainerBuilder().Build();
        string tmpDirectory = ctx.ResolveTempDirPath();
        Directory.CreateDirectory(tmpDirectory);

        File.WriteAllText(Path.Combine(tmpDirectory, EraExporter.ChecksumsSHA256FileName), "");

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

        string accPath = Path.Combine(tmpDirectory, EraExporter.AccumulatorFileName);
        HashSet<ValueHash256> trusted = [];
        foreach (string line in await File.ReadAllLinesAsync(accPath))
            trusted.Add(EraPathUtils.ExtractHashFromChecksumEntry(line));

        using IEraStore eraStore = ctx.Resolve<IEraStoreFactory>().Create(tmpDirectory, trusted);

        Assert.That(
            async () => await eraStore.FindBlockAndReceipts(eraStore.BlockRange.First, ensureValidated: true),
            Throws.Nothing);
    }

    private static async Task<EraStoreEnv> CreateDefaultEraStoreEnv(int chainLength = 32)
    {
        IContainer ctx = await EraETestModule.CreateExportedEraEnv(chainLength, from: 0, to: 0);
        string tmpDirectory = ctx.ResolveTempDirPath();
        IEraStore eraStore = ctx.Resolve<IEraStoreFactory>().Create(tmpDirectory, null);
        return new EraStoreEnv(ctx, eraStore);
    }
}

file static class ContainerAsyncExtension
{
    public static Task<IContainer> AsTask(this IContainer container) => Task.FromResult(container);
}

internal sealed record EraStoreEnv(IContainer Ctx, IEraStore EraStore) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        EraStore.Dispose();
        await Ctx.DisposeAsync();
    }
}

