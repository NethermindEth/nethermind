// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using NSubstitute;
using NSubstitute.ReturnsExtensions;
using System.IO.Abstractions;
using Autofac;
using Nethermind.Era1.Exceptions;

namespace Nethermind.Era1.Test;

public class EraExporterTests
{
    [TestCase(1, 0UL, 1UL - 1, 1UL, 1)]
    [TestCase(3, 0UL, 3UL - 1, 1UL, 3)]
    [TestCase(16, 0UL, 16UL - 1, 16UL, 1)]
    [TestCase(16, 0UL, 0UL, 16UL, 1)]
    [TestCase(32, 0UL, 32UL - 1, 16UL, 2)]
    [TestCase(32, 8UL, 0UL, 16UL, 2)]
    [TestCase(48, 8UL, 40UL - 1, 16UL, 2)]
    [TestCase(64 * 2 + 1, 0UL, 64UL * 2 + 1 - 1, 64UL, 3)]
    public async Task Export_ChainHasDifferentLength_CorrectNumberOfFilesCreated_WithFileName(int chainLength, ulong start, ulong end, ulong size, int expectedNumberOfFiles)
    {
        await using IContainer container = EraTestModule.BuildContainerBuilderWithBlockTreeOfLength(chainLength)
            .AddSingleton<IEraConfig>(new EraConfig() { MaxEra1Size = size })
            .Build();

        string tmpDirectory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(tmpDirectory, start, end);

        int fileCount = container.Resolve<IFileSystem>().Directory.GetFiles(tmpDirectory).Length;
        int metaFile = 2;
        Assert.That(fileCount, Is.EqualTo(expectedNumberOfFiles + metaFile));
        string[] files = Directory.GetFiles(tmpDirectory);
        foreach (string file in files)
        {
            if (Path.GetFileName(file).Equals("accumulators.txt") || Path.GetFileName(file).Equals("checksums.txt"))
            {
                foreach (string line in File.ReadLines(file))
                {
                    ReadOnlySpan<char> lineSpan = line.AsSpan();
                    int spaceIndex = lineSpan.IndexOf(' ');

                    Assert.That(spaceIndex, Is.GreaterThan(0)); // not at beginning
                    Assert.That(spaceIndex, Is.LessThan(lineSpan.Length - 1)); // not at end

                    int secondSpaceIndex = lineSpan[(spaceIndex + 1)..].IndexOf(' ');
                    Assert.That(secondSpaceIndex, Is.EqualTo(-1), "More than two words found");

                    ReadOnlySpan<char> secondWord = lineSpan[(spaceIndex + 1)..];
                    Assert.That(secondWord.EndsWith(".era1".AsSpan()));
                }
            }
        }
    }

    [TestCase(1, 1UL)]
    [TestCase(2, 2UL)]
    [TestCase(2, 3UL)]
    [TestCase(99, 999UL)]
    public void Export_ExportBeyondAvailableBlocks_ThrowEraException(int chainLength, ulong to)
    {
        using IContainer container = EraTestModule.BuildContainerBuilderWithBlockTreeOfLength(chainLength)
            .Build();

        IEraExporter sut = container.Resolve<IEraExporter>();
        string tmpDirectory = container.ResolveTempDirPath();

        Assert.That(() => sut.Export(tmpDirectory, 0UL, to), Throws.TypeOf<ArgumentException>());
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

        Assert.That(() => sut.Export(tmpDirectory, 0UL, 1UL), Throws.TypeOf<EraException>());
    }
}
