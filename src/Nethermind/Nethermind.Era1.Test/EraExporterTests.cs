// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using NSubstitute;
using NSubstitute.ReturnsExtensions;
using System.IO.Abstractions;
using Autofac;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.IO;
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
                    string fileName = secondWord.ToString();
                    Assert.That(File.Exists(Path.Combine(tmpDirectory, fileName)), $"{fileName} does not exist");
                }
            }
        }
    }

    [TestCase(0UL, 31UL, 32UL, 63UL, false, false)]
    [TestCase(32UL, 63UL, 0UL, 31UL, false, false)]
    [TestCase(0UL, 47UL, 16UL, 63UL, false, false)]
    [TestCase(0UL, 31UL, 32UL, 63UL, true, false)]
    [TestCase(32UL, 63UL, 16UL, 31UL, false, true)]
    [TestCase(16UL, 47UL, 48UL, 63UL, false, true)]
    [TestCase(0UL, 40UL, 0UL, 63UL, false, false)]
    public async Task Export_IntoDirectoryWithEarlierExport_WritesManifestsOfWholeDirectory(
        ulong firstFrom, ulong firstTo, ulong secondFrom, ulong secondTo, bool removeFirstEraFile, bool hashOnly)
    {
        const ulong eraSize = 16;
        await using IContainer container = EraTestModule.BuildContainerBuilderWithBlockTreeOfLength(64).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, firstFrom, firstTo);

        // Rewrite the earlier manifests as older exports wrote them, naming each epoch's placeholder file, or as hash-only
        // (geth-style) lines matched by position, and give the epochs the second export rewrites stale hashes that must be
        // replaced, not kept.
        foreach (string manifest in new[] { EraExporter.AccumulatorFileName, EraExporter.ChecksumsFileName })
        {
            string path = Path.Combine(directory, manifest);
            string[] lines = await File.ReadAllLinesAsync(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string[] columns = lines[i].Split(' ');
                ulong epoch = ulong.Parse(columns[1].Split('-')[1]);
                string hash = epoch >= secondFrom / eraSize && epoch <= secondTo / eraSize ? $"0x{new string('0', 64)}" : columns[0];
                lines[i] = hashOnly ? hash : $"{hash} {EraPathUtils.Filename(EraTestModule.TestNetwork, epoch, Keccak.Zero)}";
            }
            await File.WriteAllLinesAsync(path, lines);
        }

        ulong from = Math.Min(firstFrom, secondFrom);
        if (removeFirstEraFile)
        {
            File.Delete(Directory.GetFiles(directory, "*.era1").Order().First());
            from += eraSize;
        }

        await sut.Export(directory, secondFrom, secondTo);

        using TempPath reference = TempPath.GetTempDirectory();
        await sut.Export(reference.Path, from, Math.Max(firstTo, secondTo));
        using IEraStore eraStore = container.Resolve<IEraStoreFactory>().Create(directory, null);

        using (Assert.EnterMultipleScope())
        {
            foreach (string manifest in new[] { EraExporter.AccumulatorFileName, EraExporter.ChecksumsFileName })
            {
                Assert.That(await File.ReadAllLinesAsync(Path.Combine(directory, manifest)),
                    Is.EqualTo(await File.ReadAllLinesAsync(Path.Combine(reference.Path, manifest))), manifest);
            }

            Assert.That(Directory.GetFiles(directory, "*.era1"), Has.Length.EqualTo((int)((64 - from) / eraSize)), "one file per epoch");
            for (ulong block = from; block < 64; block += eraSize)
            {
                Assert.That(async () => await eraStore.FindBlockAndReceipts(block), Throws.Nothing, $"block {block}");
            }

            Block source = container.Resolve<IBlockTree>().FindBlock(45, BlockTreeLookupOptions.None)!;
            (Block? read, TxReceipt[]? receipts) = await eraStore.FindBlockAndReceipts(45);
            Assert.That(read?.Hash, Is.EqualTo(source.Hash));
            Assert.That(receipts?.Select(static r => r.GasUsedTotal), Is.EqualTo(container.Resolve<IReceiptStorage>().Get(source).Select(static r => r.GasUsedTotal)));
        }
    }

    [Test]
    public async Task Export_WhenEpochHasFinishedPlaceholder_ReplacesItAndKeepsUnrelatedFiles()
    {
        await using IContainer container = EraTestModule.BuildContainerBuilderWithBlockTreeOfLength(16).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, 0, 15);
        string finalName = Path.GetFileName(Directory.GetFiles(directory, "*.era1").Single());

        // Finished and listed by the manifests: only its name tells that the export never published it.
        string placeholderName = EraPathUtils.Filename(EraTestModule.TestNetwork, 0, Keccak.Zero);
        File.Move(Path.Combine(directory, finalName), Path.Combine(directory, placeholderName));
        foreach (string manifest in new[] { EraExporter.AccumulatorFileName, EraExporter.ChecksumsFileName })
        {
            string path = Path.Combine(directory, manifest);
            await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace(finalName, placeholderName));
        }

        string hash = finalName.Split('-')[2][..8];
        string[] unrelated = [$"xyz-00000-{hash}.era1", $"abc-00001-{hash}.era1", $"abc-00000-{hash}-noproofs.ere", $"abc-00000-{hash}.txt", $"abc-00000-{hash}.era1.bak"];
        foreach (string name in unrelated) File.Copy(Path.Combine(directory, placeholderName), Path.Combine(directory, name));

        await sut.Export(directory, 0, 15);

        Assert.That(Directory.GetFiles(directory).Select(Path.GetFileName),
            Is.EquivalentTo(unrelated.Append(finalName).Concat([EraExporter.AccumulatorFileName, EraExporter.ChecksumsFileName])));
        using IEraStore eraStore = container.Resolve<IEraStoreFactory>().Create(directory, null);
        Assert.That(async () => await eraStore.FindBlockAndReceipts(15), Throws.Nothing);
    }

    [Test]
    public async Task Export_WhenItWouldLoseBlocksOfEarlierFile_ThrowsBeforeWriting()
    {
        await using IContainer container = EraTestModule.BuildContainerBuilderWithBlockTreeOfLength(48).Build();
        string directory = container.ResolveTempDirPath();
        IEraExporter sut = container.Resolve<IEraExporter>();
        await sut.Export(directory, 32, 47);
        string[] before = Directory.GetFiles(directory);

        Assert.That(() => sut.Export(directory, 32, 40), Throws.TypeOf<EraException>().With.Message.Contains("(blocks 32-47)"));
        Assert.That(Directory.GetFiles(directory), Is.EquivalentTo(before));
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

        Assert.That(() => { _ = sut.Export(tmpDirectory, 0UL, to); }, Throws.TypeOf<ArgumentException>());
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
