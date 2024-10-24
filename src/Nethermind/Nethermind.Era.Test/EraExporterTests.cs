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
    [TestCase(1, 1, 1)]
    [TestCase(3, 1, 3)]
    [TestCase(16, 16, 1)]
    [TestCase(32, 16, 2)]
    [TestCase(64 * 2 + 1, 64, 3)]
    public async Task Export_ChainHasDifferentLength_CorrectNumberOfFilesCreated(int chainlength, int size, int expectedNumberOfFiles)
    {
        await using IContainer container = EraTestModule.BuildContainerBuilderWithBlockTreeOfLength(chainlength)
            .AddSingleton<IEraConfig>(new EraConfig() { MaxEra1Size = size })
            .Build();

        TmpDirectory tmpDirectory = container.Resolve<TmpDirectory>();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(tmpDirectory.DirectoryPath, 0, chainlength - 1, createAccumulator: false);

        int fileCount = container.Resolve<IFileSystem>().Directory.GetFiles(tmpDirectory.DirectoryPath).Length;
        Assert.That(fileCount, Is.EqualTo(expectedNumberOfFiles));
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
        TmpDirectory tmpDirectory = container.Resolve<TmpDirectory>();

        Assert.That(() => sut.Export(tmpDirectory.DirectoryPath, 0, to), Throws.TypeOf<EraException>());
    }

    [Test]
    public void Export_ReceiptsAreNull_ThrowEraException()
    {
        using IContainer container = EraTestModule.BuildContainerBuilderWithBlockTreeOfLength(10)
            .Build();

        TmpDirectory tmpDirectory = container.Resolve<TmpDirectory>();
        container.Resolve<IReceiptStorage>().Get(Arg.Any<Block>(), Arg.Any<bool>()).ReturnsNull();

        IEraExporter sut = container.Resolve<IEraExporter>();

        Assert.That(() => sut.Export(tmpDirectory.DirectoryPath, 0, 1), Throws.TypeOf<EraException>());
    }
}
