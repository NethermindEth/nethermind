// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using NSubstitute;
using NSubstitute.ReturnsExtensions;
using System.IO.Abstractions;
using Autofac;

namespace Nethermind.Era1.Test;
public class EraExporterTests
{
    [TestCase(1, 0, 1 - 1, 1, 1)]
    [TestCase(3, 0, 3 - 1, 1, 3)]
    [TestCase(16, 0, 16 - 1, 16, 1)]
    [TestCase(16, 0, 0, 16, 1)]
    [TestCase(32, 0, 32 - 1, 16, 2)]
    [TestCase(32, 8, 0, 16, 2)]
    [TestCase(48, 8, 40 - 1, 16, 2)]
    [TestCase(64 * 2 + 1, 0, 64 * 2 + 1 - 1, 64, 3)]
    public async Task Export_ChainHasDifferentLength_CorrectNumberOfFilesCreated(int chainlength, int start, int end, int size, int expectedNumberOfFiles)
    {
        await using IContainer container = EraTestModule.BuildContainerBuilderWithBlockTreeOfLength(chainlength)
            .AddSingleton<IEraConfig>(new EraConfig() { MaxEra1Size = size })
            .Build();

        string tmpDirectory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(tmpDirectory, start, end);

        int fileCount = container.Resolve<IFileSystem>().Directory.GetFiles(tmpDirectory).Length;
        int metaFile = 2;
        Assert.That(fileCount, Is.EqualTo(expectedNumberOfFiles + metaFile));
    }

    [TestCase(1, 1)]
    [TestCase(2, 2)]
    [TestCase(2, 3)]
    [TestCase(99, 999)]
    public void Export_ExportBeyondAvailableBlocks_ThrowEraException(int chainLength, int to)
    {
        using IContainer container = EraTestModule.BuildContainerBuilderWithBlockTreeOfLength(chainLength)
            .Build();

        IEraExporter sut = container.Resolve<IEraExporter>();
        string tmpDirectory = container.ResolveTempDirPath();

        Assert.That(() => sut.Export(tmpDirectory, 0, to), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void Export_ReceiptsAreNull_ThrowEraException()
    {
        using IContainer container = EraTestModule.BuildContainerBuilderWithBlockTreeOfLength(10)
            .AddSingleton<IReceiptStorage>(Substitute.For<IReceiptStorage>())
            .Build();

        string tmpDirectory = container.ResolveTempDirPath();
        container.Resolve<IReceiptStorage>().Get(Arg.Any<Block>(), Arg.Any<bool>(), Arg.Any<bool>()).ReturnsNull();

        IEraExporter sut = container.Resolve<IEraExporter>();

        Assert.That(() => sut.Export(tmpDirectory, 0, 1), Throws.TypeOf<EraException>());
    }
}
