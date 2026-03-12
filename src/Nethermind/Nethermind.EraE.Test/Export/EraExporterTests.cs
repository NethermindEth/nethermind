// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using FluentAssertions;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using NSubstitute;
using NSubstitute.ReturnsExtensions;
using Nethermind.EraE.Config;
using Nethermind.EraE.Exceptions;
using Nethermind.EraE.Export;
using NUnit.Framework;

namespace Nethermind.EraE.Test.Export;

public class EraExporterTests
{
    [TestCase(1, 0, 0, 1, 1)]
    [TestCase(3, 0, 2, 1, 3)]
    [TestCase(16, 0, 15, 16, 1)]
    [TestCase(32, 0, 31, 16, 2)]
    [TestCase(48, 8, 39, 16, 3)]
    public async Task Export_WithVaryingChainLength_CreatesCorrectNumberOfEpochFiles(
        int chainLength, int from, int to, int eraSize, int expectedEraFiles)
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
        eraFiles.Length.Should().Be(expectedEraFiles);
    }

    [Test]
    public async Task Export_WhenCalled_CreatesChecksumsFile()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(32).Build();

        string tmpDirectory = container.ResolveTempDirPath();
        await container.Resolve<IEraExporter>().Export(tmpDirectory, 0, 0);

        System.IO.File.Exists(System.IO.Path.Combine(tmpDirectory, EraExporter.ChecksumsFileName))
            .Should().BeTrue();
    }

    [Test]
    public async Task Export_WhenCalled_CreatesAccumulatorsFile()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(32).Build();

        string tmpDirectory = container.ResolveTempDirPath();
        await container.Resolve<IEraExporter>().Export(tmpDirectory, 0, 0);

        System.IO.File.Exists(System.IO.Path.Combine(tmpDirectory, EraExporter.AccumulatorFileName))
            .Should().BeTrue();
    }

    [Test]
    public async Task Export_WhenCalled_ChecksumsFileHasCorrectFormat()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(32).Build();

        string tmpDirectory = container.ResolveTempDirPath();
        await container.Resolve<IEraExporter>().Export(tmpDirectory, 0, 0);

        string[] lines = await System.IO.File.ReadAllLinesAsync(
            System.IO.Path.Combine(tmpDirectory, EraExporter.ChecksumsFileName));

        foreach (string line in lines)
        {
            ReadOnlySpan<char> span = line.AsSpan();
            int spaceIdx = span.IndexOf(' ');
            spaceIdx.Should().BeGreaterThan(0, "each line should be '<hash> <filename>'");
            span[(spaceIdx + 1)..].EndsWith(EraPathUtils.FileExtension.AsSpan()).Should().BeTrue();
        }
    }

    [Test]
    public async Task Export_WhenCalled_EraFilesHaveCorrectExtension()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(16).Build();

        string tmpDirectory = container.ResolveTempDirPath();
        await container.Resolve<IEraExporter>().Export(tmpDirectory, 0, 0);

        string[] eraFiles = System.IO.Directory.GetFiles(tmpDirectory, $"*{EraPathUtils.FileExtension}");
        eraFiles.Should().NotBeEmpty();
        eraFiles.Should().AllSatisfy(f => f.Should().EndWith(EraPathUtils.FileExtension));
    }

    [Test]
    public async Task Export_WithToExceedingHeadBlock_ThrowsArgumentException()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(5).Build();

        string tmpDirectory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();

        Assert.That(() => sut.Export(tmpDirectory, 0, 999), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public async Task Export_WithFromExceedingTo_ThrowsArgumentException()
    {
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(32).Build();

        string tmpDirectory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();

        Assert.That(() => sut.Export(tmpDirectory, 20, 10), Throws.TypeOf<ArgumentException>());
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

        Assert.That(() => sut.Export(tmpDirectory, 0, 1), Throws.TypeOf<EraException>());
    }

    [Test]
    public async Task Export_WithToZero_ExportsUpToHead()
    {
        const int chainLength = 32;
        await using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(chainLength).Build();

        string tmpDirectory = container.ResolveTempDirPath();
        await container.Resolve<IEraExporter>().Export(tmpDirectory, 0, to: 0);

        string[] eraFiles = System.IO.Directory.GetFiles(tmpDirectory, $"*{EraPathUtils.FileExtension}");
        eraFiles.Should().NotBeEmpty("exporting to 0 should default to head");
    }

    [Test]
    public void Export_WithDestinationAsExistingFile_ThrowsArgumentException()
    {
        using IContainer container = EraETestModule.BuildContainerBuilderWithBlockTreeOfLength(10).Build();

        string tmpFile = container.ResolveTempFilePath();
        System.IO.File.WriteAllText(tmpFile, "existing");

        IEraExporter sut = container.Resolve<IEraExporter>();
        Assert.That(() => sut.Export(tmpFile, 0, 0), Throws.TypeOf<ArgumentException>());
    }
}
