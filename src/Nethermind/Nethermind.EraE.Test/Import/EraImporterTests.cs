// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using EraException = Nethermind.Era1.EraException;
using EraVerificationException = Nethermind.Era1.Exceptions.EraVerificationException;
using Nethermind.EraE.Export;
using Nethermind.EraE.Import;
using NUnit.Framework;

namespace Nethermind.EraE.Test.Import;

public class EraImporterTests
{
    [Test]
    public Task Import_WithNonExistentDirectory_ThrowsArgumentException()
    {
        using IContainer ctx = EraETestModule.BuildContainerBuilder().Build();

        IEraImporter sut = ctx.Resolve<IEraImporter>();
        Assert.That(
            () => sut.Import("/nonexistent/path", 0, 0, null),
            Throws.TypeOf<ArgumentException>());
        return Task.CompletedTask;
    }

    [Test]
    public async Task Import_WithEmptyDirectory_ThrowsEraException()
    {
        using IContainer ctx = EraETestModule.BuildContainerBuilder().Build();

        string tmpDirectory = ctx.ResolveTempDirPath();
        System.IO.Directory.CreateDirectory(tmpDirectory);
        await System.IO.File.WriteAllTextAsync(
            System.IO.Path.Combine(tmpDirectory, EraExporter.ChecksumsFileName), "");

        IEraImporter sut = ctx.Resolve<IEraImporter>();
        Assert.That(
            () => sut.Import(tmpDirectory, 0, 0, null),
            Throws.TypeOf<EraException>());
    }

    [Test]
    public async Task Import_WithValidEraFiles_ImportsAllBlocksIntoTree()
    {
        const int chainLength = 32;
        await using IContainer sourceCtx = await EraETestModule.CreateExportedEraEnv(chainLength, from: 0, to: 0);
        string exportPath = sourceCtx.ResolveTempDirPath();

        IBlockTree sourceTree = sourceCtx.Resolve<IBlockTree>();
        BlockTree targetTree = Build.A.BlockTree()
            .WithBlocks(sourceTree.FindBlock(0, BlockTreeLookupOptions.None)!)
            .TestObject;

        await using IContainer targetCtx = EraETestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(targetTree)
            .Build();

        IEraImporter sut = targetCtx.Resolve<IEraImporter>();
        await sut.Import(exportPath, 0, long.MaxValue, null);

        for (long i = 1; i < chainLength; i++)
        {
            targetTree.FindBlock(i, BlockTreeLookupOptions.None).Should().NotBeNull(
                $"block {i} should have been imported");
        }
    }

    [Test]
    public async Task Import_WithTrustedAccumulators_Succeeds()
    {
        const int chainLength = 32;
        await using IContainer sourceCtx = await EraETestModule.CreateExportedEraEnv(chainLength, from: 0, to: 0);
        string exportPath = sourceCtx.ResolveTempDirPath();

        string accumulatorPath = System.IO.Path.Combine(exportPath, EraExporter.AccumulatorFileName);

        IBlockTree sourceTree = sourceCtx.Resolve<IBlockTree>();
        BlockTree targetTree = Build.A.BlockTree()
            .WithBlocks(sourceTree.FindBlock(0, BlockTreeLookupOptions.None)!)
            .TestObject;

        await using IContainer targetCtx = EraETestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(targetTree)
            .Build();

        IEraImporter sut = targetCtx.Resolve<IEraImporter>();

        Assert.That(
            () => sut.Import(exportPath, 0, long.MaxValue, accumulatorPath),
            Throws.Nothing);
    }

    [Test]
    public async Task Import_WithModifiedChecksum_ThrowsEraVerificationException()
    {
        await using IContainer sourceCtx = await EraETestModule.CreateExportedEraEnv(32, from: 0, to: 0);
        string exportPath = sourceCtx.ResolveTempDirPath();

        string checksumPath = System.IO.Path.Combine(exportPath, EraExporter.ChecksumsFileName);
        string[] lines = await System.IO.File.ReadAllLinesAsync(checksumPath);
        lines[^1] = "0x0000000000000000000000000000000000000000000000000000000000000000 " +
                    System.IO.Path.GetFileName(lines[^1].Split(' ')[^1]);
        await System.IO.File.WriteAllLinesAsync(checksumPath, lines);

        IBlockTree sourceTree = sourceCtx.Resolve<IBlockTree>();
        BlockTree targetTree = Build.A.BlockTree()
            .WithBlocks(sourceTree.FindBlock(0, BlockTreeLookupOptions.None)!)
            .TestObject;

        await using IContainer targetCtx = EraETestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(targetTree)
            .Build();

        IEraImporter sut = targetCtx.Resolve<IEraImporter>();

        Assert.That(
            () => sut.Import(exportPath, 0, long.MaxValue, null),
            Throws.TypeOf<EraVerificationException>());
    }

    [Test]
    public async Task Import_WithWrongTrustedAccumulator_ThrowsEraVerificationException()
    {
        await using IContainer sourceCtx = await EraETestModule.CreateExportedEraEnv(32, from: 0, to: 0);
        string exportPath = sourceCtx.ResolveTempDirPath();

        string fakeAccumulatorPath = System.IO.Path.Combine(exportPath, "fake_accumulators.txt");
        string[] accLines = await System.IO.File.ReadAllLinesAsync(
            System.IO.Path.Combine(exportPath, EraExporter.AccumulatorFileName));
        string[] fakeLines = accLines.Select(l =>
            "0x0000000000000000000000000000000000000000000000000000000000000000 " +
            l.Split(' ')[^1]).ToArray();
        await System.IO.File.WriteAllLinesAsync(fakeAccumulatorPath, fakeLines);

        IBlockTree sourceTree = sourceCtx.Resolve<IBlockTree>();
        BlockTree targetTree = Build.A.BlockTree()
            .WithBlocks(sourceTree.FindBlock(0, BlockTreeLookupOptions.None)!)
            .TestObject;

        await using IContainer targetCtx = EraETestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(targetTree)
            .Build();

        IEraImporter sut = targetCtx.Resolve<IEraImporter>();

        Assert.That(
            () => sut.Import(exportPath, 0, long.MaxValue, fakeAccumulatorPath),
            Throws.TypeOf<EraVerificationException>());
    }

    [Test]
    public async Task Import_WhenBlockFailsValidation_ThrowsEraVerificationException()
    {
        await using IContainer sourceCtx = await EraETestModule.CreateExportedEraEnv(32, from: 0, to: 0);
        string exportPath = sourceCtx.ResolveTempDirPath();

        IBlockTree sourceTree = sourceCtx.Resolve<IBlockTree>();
        BlockTree targetTree = Build.A.BlockTree()
            .WithBlocks(sourceTree.FindBlock(0, BlockTreeLookupOptions.None)!)
            .TestObject;

        await using IContainer targetCtx = EraETestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(targetTree)
            .AddSingleton<IBlockValidator>(Always.Invalid)
            .Build();

        IEraImporter sut = targetCtx.Resolve<IEraImporter>();

        Assert.That(
            () => sut.Import(exportPath, 0, long.MaxValue, null),
            Throws.TypeOf<EraVerificationException>());
    }
}
