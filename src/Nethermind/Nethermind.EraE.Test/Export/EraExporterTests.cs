// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NSubstitute;
using NSubstitute.ReturnsExtensions;
using Nethermind.EraE.Config;
using Nethermind.EraE.E2Store;
using EraException = Nethermind.Era1.Exceptions.EraException;
using Nethermind.EraE.Export;
using NUnit.Framework;

namespace Nethermind.EraE.Test.Export;

public class EraExporterTests
{
    [TestCase(1, 0UL, 0UL, 1UL, 1)]
    [TestCase(3, 0UL, 2UL, 1UL, 3)]
    [TestCase(16, 0UL, 15UL, 16UL, 1)]
    [TestCase(32, 0UL, 31UL, 16UL, 2)]
    [TestCase(48, 8UL, 39UL, 16UL, 3)]
    public async Task Export_WithVaryingChainLength_CreatesCorrectNumberOfEpochFiles(
        int chainLength, ulong from, ulong to, ulong eraSize, int expectedEraFiles)
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(chainLength)
            .AddSingleton<IEraEConfig>(new EraEConfig
            {
                MaxEraSize = eraSize,
                NetworkName = EraETestModule.TestNetwork
            })
            .Build();

        string tmpDirectory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(tmpDirectory, from, to);

        string[] allFiles = System.IO.Directory.GetFiles(tmpDirectory);
        string[] eraFiles = allFiles.Where(f => f.EndsWith(EraPathUtils.FileExtension)).ToArray();
        Assert.That(eraFiles.Length, Is.EqualTo(expectedEraFiles));
    }

    [Test]
    public async Task Export_WhenLegacyEraeFileExists_SkipsEpochWithoutWritingEre()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(16)
            .AddSingleton<IEraEConfig>(new EraEConfig { MaxEraSize = 16, NetworkName = EraETestModule.TestNetwork })
            .Build();

        string tmpDirectory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(tmpDirectory, 0, 15);

        string ereFile = System.IO.Directory.GetFiles(tmpDirectory, $"*{EraPathUtils.FileExtension}").Single();
        string eraeFile = System.IO.Path.ChangeExtension(ereFile, EraPathUtils.LegacyFileExtension);
        System.IO.File.Move(ereFile, eraeFile);

        await sut.Export(tmpDirectory, 0, 15);

        Assert.That(System.IO.File.Exists(eraeFile), Is.True, "pre-existing legacy .erae file must be kept on re-export");
        Assert.That(System.IO.Directory.GetFiles(tmpDirectory, $"*{EraPathUtils.FileExtension}"), Is.Empty,
            "epoch already present as .erae must be skipped, not re-exported as a duplicate .ere");
    }

    [TestCase("checksums_sha256.txt")]
    [TestCase("checksums.txt")]
    [TestCase("accumulators.txt")]
    public async Task Export_WhenCalled_CreatesMetadataFile(string fileName)
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(32).Build();

        string tmpDirectory = container.ResolveTempDirPath();
        await container.Resolve<IEraExporter>().Export(tmpDirectory, 0UL, 0UL);

        Assert.That(System.IO.File.Exists(System.IO.Path.Combine(tmpDirectory, fileName)), Is.True);
    }

    [Test]
    public async Task Export_WhenCalled_ChecksumsFileHasCorrectFormat()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(32).Build();

        string tmpDirectory = container.ResolveTempDirPath();
        await container.Resolve<IEraExporter>().Export(tmpDirectory, 0UL, 0UL);

        string[] lines = await System.IO.File.ReadAllLinesAsync(
            System.IO.Path.Combine(tmpDirectory, EraExporter.ChecksumsSHA256FileName));

        foreach (string line in lines)
        {
            ReadOnlySpan<char> span = line.AsSpan();
            int spaceIdx = span.IndexOf(' ');
            Assert.That(spaceIdx, Is.GreaterThan(0), "each line should be '<hash> <filename>'");
            Assert.That(span[(spaceIdx + 1)..].EndsWith(EraPathUtils.FileExtension.AsSpan()), Is.True);
        }
    }

    [Test]
    public async Task Export_WhenCalled_EraFilesHaveCorrectExtension()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(16).Build();

        string tmpDirectory = container.ResolveTempDirPath();
        await container.Resolve<IEraExporter>().Export(tmpDirectory, 0UL, 0UL);

        string[] eraFiles = System.IO.Directory.GetFiles(tmpDirectory, $"*{EraPathUtils.FileExtension}");
        Assert.That(eraFiles, Is.Not.Empty);
        foreach (string eraFile in eraFiles)
        {
            Assert.That(eraFile, Does.EndWith(EraPathUtils.FileExtension));
        }
    }

    [Test]
    public async Task Export_WithToExceedingHeadBlock_ThrowsArgumentException()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(5).Build();

        string tmpDirectory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();

        Assert.That(() => sut.Export(tmpDirectory, 0UL, 999UL), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public async Task Export_WithFromExceedingTo_ThrowsArgumentException()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(32).Build();

        string tmpDirectory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();

        Assert.That(() => sut.Export(tmpDirectory, 20UL, 10UL), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public async Task Export_WhenReceiptsStorageReturnsNull_ThrowsEraException()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(10)
            .AddSingleton<IReceiptStorage>(Substitute.For<IReceiptStorage>())
            .Build();

        container.Resolve<IReceiptStorage>()
            .Get(Arg.Any<Block>(), Arg.Any<bool>(), Arg.Any<bool>())
            .ReturnsNull();

        IEraExporter sut = container.Resolve<IEraExporter>();
        string tmpDirectory = container.ResolveTempDirPath();

        Assert.That(() => sut.Export(tmpDirectory, 0UL, 1UL), Throws.TypeOf<EraException>());
    }

    [Test]
    public async Task Export_WithToZero_ExportsUpToHead()
    {
        const int chainLength = 32;
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(chainLength).Build();

        string tmpDirectory = container.ResolveTempDirPath();
        await container.Resolve<IEraExporter>().Export(tmpDirectory, 0UL, to: 0UL);

        string[] eraFiles = System.IO.Directory.GetFiles(tmpDirectory, $"*{EraPathUtils.FileExtension}");
        Assert.That(eraFiles, Is.Not.Empty, "exporting to 0 should default to head");
    }

    [Test]
    public void Export_WithDestinationAsExistingFile_ThrowsArgumentException()
    {
        using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(10).Build();

        string tmpFile = container.ResolveTempFilePath();
        System.IO.File.WriteAllText(tmpFile, "existing");

        IEraExporter sut = container.Resolve<IEraExporter>();
        Assert.That(() => sut.Export(tmpFile, 0UL, 0UL), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public async Task Export_WithPostMergeChain_ProducesEpochWithNoTdProofOrAccumulatorEntries()
    {
        const ulong chainLength = 16;
        await using IContainer container = EraETestModule.BuildContainerBuilderWithPostMergeBlockTreeOfLength(chainLength).Build();

        string tmpDirectory = container.ResolveTempDirPath();
        // Export from block 1: genesis is pre-merge in all block trees, so starting at 1 ensures a pure post-merge epoch.
        await container.Resolve<IEraExporter>().Export(tmpDirectory, 1UL, 0UL);

        string[] eraFiles = Directory.GetFiles(tmpDirectory, $"*{EraPathUtils.FileExtension}");
        Assert.That(eraFiles, Has.Length.EqualTo(1), "one epoch for blocks 1-15");

        List<ushort> types = EraFileFormatComplianceTests.ReadAllEntries(eraFiles[0]).Select(e => e.Type).ToList();
        Assert.That(types, Does.Not.Contain(EntryTypes.Proof), "post-merge epochs have no Proof entries");
        Assert.That(types, Does.Not.Contain(EntryTypes.TotalDifficulty), "post-merge epochs have no TotalDifficulty entries");
        Assert.That(types, Does.Not.Contain(EntryTypes.AccumulatorRoot), "post-merge epochs have no AccumulatorRoot entry");
    }

    [Test]
    public void Export_WhenLastBlockBodyNotAvailable_ThrowsInvalidOperationException()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        Block head = Build.A.Block.WithNumber(10).TestObject;
        blockTree.Head.Returns(head);
        blockTree.FindBlock(10, BlockTreeLookupOptions.DoNotCreateLevelIfMissing).ReturnsNull();

        using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(1)
            .AddSingleton<IBlockTree>(blockTree)
            .Build();

        string tmpDirectory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();

        Assert.That(() => sut.Export(tmpDirectory, 0UL, 10UL), Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public async Task Export_WithPartialRange_CreatesOnlyEpochsInRange()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(48)
            .AddSingleton<IEraEConfig>(new EraEConfig { MaxEraSize = 16, NetworkName = EraETestModule.TestNetwork })
            .Build();

        string tmpDirectory = container.ResolveTempDirPath();
        await container.Resolve<IEraExporter>().Export(tmpDirectory, 16UL, 31UL);

        string[] eraFiles = System.IO.Directory.GetFiles(tmpDirectory, $"*{EraPathUtils.FileExtension}");
        Assert.That(eraFiles.Length, Is.EqualTo(1), "only one epoch covers blocks 16-31");
    }
}
